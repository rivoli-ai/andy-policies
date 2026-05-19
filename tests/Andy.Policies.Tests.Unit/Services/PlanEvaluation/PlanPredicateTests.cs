// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.PlanEvaluation;
using Andy.Policies.Infrastructure.Services.PlanEvaluation;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services.PlanEvaluation;

/// <summary>
/// Unit tests for the five plan-aware predicates pinned by
/// rivoli-ai/andy-policies#232. Each predicate gets:
/// <list type="bullet">
///   <item>A passing case asserting <see cref="PredicateOutcome.Pass"/>.</item>
///   <item>A failing case asserting <see cref="PredicateOutcome.Fail"/>.</item>
///   <item>Where applicable, a missing-data case asserting
///     <see cref="PredicateOutcome.Unevaluable"/> — the issue spec
///     forbids silently passing when the underlying data is null.</item>
/// </list>
/// </summary>
public class PlanPredicateTests
{
    private static PlanEvaluationGoalView View(
        IEnumerable<PlanEvaluationTaskView>? tasks = null,
        string? tier = null,
        IReadOnlyList<string>? approved = null,
        decimal? budget = null,
        decimal? estimatedCost = null,
        string? container = "ctr-1") => new(
        GoalId: Guid.NewGuid(),
        PlanVersion: "1",
        WorkspaceContainerId: container,
        WorkspaceTier: tier,
        WorkspaceApprovedAgentIds: approved,
        WorkspaceCostBudgetUsd: budget,
        Tasks: tasks?.ToList() ?? new List<PlanEvaluationTaskView>(),
        EstimatedCostUsd: estimatedCost);

    private static PlanEvaluationTaskView Task(
        IEnumerable<string>? tools = null,
        string? agentId = "coder",
        string? env = null) => new(
        TaskId: Guid.NewGuid(),
        EffectiveAgentId: agentId,
        ToolsAllowed: tools?.ToList() ?? new List<string>(),
        TargetEnv: env);

    // ---- allTasksReadOnly -------------------------------------------------

    [Fact]
    public void AllTasksReadOnly_passes_when_every_task_carries_only_read_tools()
    {
        var p = new AllTasksReadOnlyPredicate();
        var v = View(tasks: new[]
        {
            Task(tools: new[] { "read-file", "list-dir" }),
            Task(tools: new[] { "search-code" }),
        });

        var r = p.Evaluate(v);

        r.Outcome.Should().Be(PredicateOutcome.Pass);
    }

    [Theory]
    [InlineData("write-file")]
    [InlineData("shell-exec")]
    [InlineData("delete-resource")]
    [InlineData("deploy")]
    [InlineData("push-branch")]
    public void AllTasksReadOnly_fails_for_write_or_exec_tool(string tool)
    {
        var p = new AllTasksReadOnlyPredicate();
        var v = View(tasks: new[]
        {
            Task(tools: new[] { "read-file" }),
            Task(tools: new[] { tool }),
        });

        var r = p.Evaluate(v);

        r.Outcome.Should().Be(PredicateOutcome.Fail);
        r.Reason.Should().Contain("write/exec");
    }

    [Fact]
    public void AllTasksReadOnly_passes_vacuously_with_zero_tasks()
    {
        var p = new AllTasksReadOnlyPredicate();
        var r = p.Evaluate(View(tasks: Array.Empty<PlanEvaluationTaskView>()));
        r.Outcome.Should().Be(PredicateOutcome.Pass);
    }

    // ---- workspaceIsSandbox -----------------------------------------------

    [Theory]
    [InlineData("sandbox")]
    [InlineData("SANDBOX")]
    [InlineData("Sandbox")]
    public void WorkspaceIsSandbox_passes_for_sandbox_tier_case_insensitive(string tier)
    {
        var p = new WorkspaceIsSandboxPredicate();
        p.Evaluate(View(tier: tier)).Outcome.Should().Be(PredicateOutcome.Pass);
    }

    [Theory]
    [InlineData("production")]
    [InlineData("staging")]
    [InlineData("free")]
    public void WorkspaceIsSandbox_fails_for_non_sandbox_tier(string tier)
    {
        var p = new WorkspaceIsSandboxPredicate();
        p.Evaluate(View(tier: tier)).Outcome.Should().Be(PredicateOutcome.Fail);
    }

    [Fact]
    public void WorkspaceIsSandbox_is_unevaluable_when_tier_is_null()
    {
        var p = new WorkspaceIsSandboxPredicate();
        p.Evaluate(View(tier: null)).Outcome.Should().Be(PredicateOutcome.Unevaluable);
    }

    // ---- noProductionDeploy -----------------------------------------------

    [Fact]
    public void NoProductionDeploy_passes_when_no_task_targets_prod()
    {
        var p = new NoProductionDeployPredicate();
        var v = View(tasks: new[]
        {
            Task(env: "dev"),
            Task(env: "staging"),
            Task(env: null),
        });

        p.Evaluate(v).Outcome.Should().Be(PredicateOutcome.Pass);
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("PROD")]
    [InlineData("production")]
    [InlineData("Production")]
    public void NoProductionDeploy_fails_when_any_task_targets_prod(string env)
    {
        var p = new NoProductionDeployPredicate();
        var v = View(tasks: new[] { Task(env: env), Task(env: "dev") });

        var r = p.Evaluate(v);

        r.Outcome.Should().Be(PredicateOutcome.Fail);
    }

    [Fact]
    public void NoProductionDeploy_passes_vacuously_with_zero_tasks()
    {
        new NoProductionDeployPredicate()
            .Evaluate(View(tasks: Array.Empty<PlanEvaluationTaskView>()))
            .Outcome.Should().Be(PredicateOutcome.Pass);
    }

    // ---- allAgentsApproved ------------------------------------------------

    [Fact]
    public void AllAgentsApproved_passes_when_every_task_agent_is_in_the_list()
    {
        var p = new AllAgentsApprovedPredicate();
        var v = View(
            approved: new[] { "coder", "reviewer" },
            tasks: new[]
            {
                Task(agentId: "coder"),
                Task(agentId: "reviewer"),
            });

        p.Evaluate(v).Outcome.Should().Be(PredicateOutcome.Pass);
    }

    [Fact]
    public void AllAgentsApproved_fails_when_any_task_agent_is_not_approved()
    {
        var p = new AllAgentsApprovedPredicate();
        var v = View(
            approved: new[] { "coder" },
            tasks: new[]
            {
                Task(agentId: "coder"),
                Task(agentId: "external-agent"),
            });

        var r = p.Evaluate(v);

        r.Outcome.Should().Be(PredicateOutcome.Fail);
        r.Reason.Should().Contain("external-agent");
    }

    [Fact]
    public void AllAgentsApproved_fails_when_a_task_has_no_effective_agent()
    {
        var p = new AllAgentsApprovedPredicate();
        var v = View(
            approved: new[] { "coder" },
            tasks: new[] { Task(agentId: null) });

        // Issue spec: missing data must not silently pass.
        p.Evaluate(v).Outcome.Should().Be(PredicateOutcome.Fail);
    }

    [Fact]
    public void AllAgentsApproved_is_unevaluable_when_approved_list_is_null()
    {
        var p = new AllAgentsApprovedPredicate();
        p.Evaluate(View(approved: null, tasks: new[] { Task() }))
            .Outcome.Should().Be(PredicateOutcome.Unevaluable);
    }

    [Fact]
    public void AllAgentsApproved_is_case_sensitive_on_agent_slug()
    {
        var p = new AllAgentsApprovedPredicate();
        // Agent slugs are opaque IDs; case-insensitive matching would
        // conflate distinct agents — guard the contract here.
        p.Evaluate(View(
            approved: new[] { "Coder" },
            tasks: new[] { Task(agentId: "coder") })).Outcome.Should().Be(PredicateOutcome.Fail);
    }

    // ---- respectsCostBudget -----------------------------------------------

    [Fact]
    public void RespectsCostBudget_passes_when_estimated_strictly_below_budget()
    {
        new RespectsCostBudgetPredicate()
            .Evaluate(View(budget: 10m, estimatedCost: 4.99m))
            .Outcome.Should().Be(PredicateOutcome.Pass);
    }

    [Fact]
    public void RespectsCostBudget_fails_when_estimated_equals_budget()
    {
        // The spec is "< budget"; equality fails.
        new RespectsCostBudgetPredicate()
            .Evaluate(View(budget: 10m, estimatedCost: 10m))
            .Outcome.Should().Be(PredicateOutcome.Fail);
    }

    [Fact]
    public void RespectsCostBudget_fails_when_estimated_above_budget()
    {
        new RespectsCostBudgetPredicate()
            .Evaluate(View(budget: 10m, estimatedCost: 15m))
            .Outcome.Should().Be(PredicateOutcome.Fail);
    }

    [Fact]
    public void RespectsCostBudget_is_unevaluable_when_budget_missing()
    {
        new RespectsCostBudgetPredicate()
            .Evaluate(View(budget: null, estimatedCost: 5m))
            .Outcome.Should().Be(PredicateOutcome.Unevaluable);
    }

    [Fact]
    public void RespectsCostBudget_is_unevaluable_when_estimate_missing()
    {
        new RespectsCostBudgetPredicate()
            .Evaluate(View(budget: 10m, estimatedCost: null))
            .Outcome.Should().Be(PredicateOutcome.Unevaluable);
    }
}
