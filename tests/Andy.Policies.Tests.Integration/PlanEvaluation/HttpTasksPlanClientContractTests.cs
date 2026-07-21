// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using Andy.Policies.Infrastructure.Services.PlanEvaluation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Andy.Policies.Tests.Integration.PlanEvaluation;

/// <summary>
/// Contract test (rivoli-ai/andy-policies#232): drives
/// <see cref="HttpTasksPlanClient"/> against a WireMock stub that mimics
/// the andy-tasks wire DTOs (GoalDto, TaskDto, EstimatesResponse).
/// Pins the field names the policy service consumes — if andy-tasks
/// renames any of these, the stubbed responses here will diverge
/// from the real service and this test will fail.
/// </summary>
public sealed class HttpTasksPlanClientContractTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    private HttpTasksPlanClient NewClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        return new HttpTasksPlanClient(http, NullLogger<HttpTasksPlanClient>.Instance);
    }

    [Fact]
    public async Task Returns_null_when_andy_tasks_returns_404_on_goal()
    {
        var goalId = Guid.NewGuid();
        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.NotFound));

        var result = await NewClient().FetchGoalViewAsync(goalId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Projects_goal_tasks_and_planned_cost_into_view()
    {
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                  "id": "{{goalId}}",
                  "title": "Refactor X",
                  "planVersion": "3",
                  "workspace": {
                    "containerId": "ctr-42",
                    "repository": "rivoli-ai/conductor",
                    "branch": "main"
                  }
                }
                """));

        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}/tasks").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                [
                  {
                    "id": "{{taskId}}",
                    "externalId": "T-1",
                    "delegationContract": {
                      "objective": "Read code",
                      "outputFormat": "text",
                      "toolsAllowed": ["read-file", "search-code"],
                      "boundaries": []
                    },
                    "suggestedAgentIds": ["coder", "reviewer"],
                    "executorId": null
                    ,"targetEnv": "dev"
                  }
                ]
                """));

        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}/estimates").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "schemaVersion": 1,
                  "target": { "kind": "goal", "id": "00000000-0000-0000-0000-000000000000" },
                  "planned": {
                    "costUsdP50": 2.45,
                    "costUsdP90": 4.10,
                    "durationSecP50": null,
                    "durationSecP90": null,
                    "estimatedBy": "planner-v1",
                    "at": "2026-05-19T00:00:00Z",
                    "schemaVersion": 1,
                    "includesProjections": false
                  },
                  "initial": null,
                  "actual": null,
                  "varianceState": null,
                  "lastUpdatedAt": "2026-05-19T00:00:00Z"
                }
                """));

        var view = await NewClient().FetchGoalViewAsync(goalId);

        view.Should().NotBeNull();
        view!.GoalId.Should().Be(goalId);
        view.PlanVersion.Should().Be("3");
        view.WorkspaceContainerId.Should().Be("ctr-42");
        view.EstimatedCostUsd.Should().Be(2.45m);

        view.Tasks.Should().HaveCount(1);
        view.Tasks[0].TaskId.Should().Be(taskId);
        view.Tasks[0].EffectiveAgentId.Should().Be("coder",
            "with no executor pinned, the top suggested agent wins");
        view.Tasks[0].ToolsAllowed.Should().BeEquivalentTo("read-file", "search-code");
        view.Tasks[0].TargetEnv.Should().Be("dev");
    }

    [Fact]
    public async Task Preserves_missing_null_empty_and_populated_task_contract_fields()
    {
        var goalId = Guid.NewGuid();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        StubGoal(goalId, planVersion: "1");
        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}/tasks").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                [
                  { "id": "{{ids[0]}}" },
                  { "id": "{{ids[1]}}", "delegationContract": { "toolsAllowed": null }, "targetEnv": null },
                  { "id": "{{ids[2]}}", "delegationContract": { "toolsAllowed": [] }, "targetEnv": "" },
                  { "id": "{{ids[3]}}", "delegationContract": { "toolsAllowed": ["read-file"] }, "targetEnv": "prod" }
                ]
                """));
        StubEstimates(goalId, null);

        var tasks = (await NewClient().FetchGoalViewAsync(goalId))!.Tasks;

        tasks[0].ToolsAllowed.Should().BeNull();
        tasks[0].TargetEnv.Should().BeNull();
        tasks[1].ToolsAllowed.Should().BeNull();
        tasks[1].TargetEnv.Should().BeNull();
        tasks[2].ToolsAllowed.Should().BeEmpty();
        tasks[2].TargetEnv.Should().BeEmpty();
        tasks[3].ToolsAllowed.Should().Equal("read-file");
        tasks[3].TargetEnv.Should().Be("prod");
    }

    [Fact]
    public async Task Executor_pin_beats_suggested_list_for_effective_agent()
    {
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        StubGoal(goalId, planVersion: "1");
        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}/tasks").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                [
                  {
                    "id": "{{taskId}}",
                    "delegationContract": { "objective": "", "outputFormat": "", "toolsAllowed": [], "boundaries": [] },
                    "suggestedAgentIds": ["fallback"],
                    "executorId": "primary"
                  }
                ]
                """));
        StubEstimates(goalId, null);

        var view = await NewClient().FetchGoalViewAsync(goalId);

        view!.Tasks[0].EffectiveAgentId.Should().Be("primary");
    }

    [Fact]
    public async Task Estimates_404_maps_to_null_cost()
    {
        var goalId = Guid.NewGuid();
        StubGoal(goalId, planVersion: "1");
        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}/tasks").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));
        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}/estimates").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var view = await NewClient().FetchGoalViewAsync(goalId);

        view!.EstimatedCostUsd.Should().BeNull(
            "missing estimates → null cost → respectsCostBudget surfaces 'data unavailable'");
    }

    [Fact]
    public async Task Default_plan_version_when_andy_tasks_omits_the_field()
    {
        var goalId = Guid.NewGuid();
        // Older andy-tasks shapes that don't carry planVersion.
        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{ "id": "{{goalId}}", "title": "X" }"""));
        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}/tasks").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}/estimates").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var view = await NewClient().FetchGoalViewAsync(goalId);

        view!.PlanVersion.Should().Be("1");
        view.Tasks.Should().BeEmpty();
    }

    private void StubGoal(Guid goalId, string planVersion)
    {
        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                  "id": "{{goalId}}",
                  "title": "X",
                  "planVersion": "{{planVersion}}",
                  "workspace": { "containerId": "ctr-1", "repository": "r", "branch": "b" }
                }
                """));
    }

    private void StubEstimates(Guid goalId, decimal? plannedCost)
    {
        if (plannedCost is null)
        {
            _server
                .Given(Request.Create().WithPath($"/api/goals/{goalId:D}/estimates").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(404));
            return;
        }

        _server
            .Given(Request.Create().WithPath($"/api/goals/{goalId:D}/estimates").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                { "schemaVersion": 1,
                  "target": { "kind": "goal", "id": "00000000-0000-0000-0000-000000000000" },
                  "planned": { "costUsdP50": {{plannedCost}}, "estimatedBy": "p", "at": "2026-05-19T00:00:00Z", "schemaVersion": 1 } }
                """));
    }
}
