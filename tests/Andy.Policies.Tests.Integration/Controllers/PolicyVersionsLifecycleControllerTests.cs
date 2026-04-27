// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using Andy.Policies.Application.Dtos;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// Integration tests for the lifecycle endpoints (P2.3, story
/// rivoli-ai/andy-policies#13). Exercises publish/winding-down/retire over the
/// real MVC pipeline plus <c>LifecycleTransitionService</c> against a
/// SQLite-backed factory. Verifies wire contract: 200 with updated DTO on the
/// happy path, 400 on missing rationale, 404 on unknown ids, 409 on disallowed
/// transitions, and that auto-supersede flips the previous Active to
/// WindingDown atomically.
/// </summary>
public class PolicyVersionsLifecycleControllerTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    public PolicyVersionsLifecycleControllerTests(PoliciesApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static CreatePolicyRequest MinimalCreate(string name) => new(
        Name: name,
        Description: null,
        Summary: "summary",
        Enforcement: "Must",
        Severity: "Critical",
        Scopes: Array.Empty<string>(),
        RulesJson: "{}");

    private static LifecycleTransitionRequest WithRationale(string rationale = "ship-it")
        => new(rationale);

    private async Task<PolicyVersionDto> CreateDraftAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/policies", MinimalCreate(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
    }

    private async Task<PolicyVersionDto> BumpDraftAsync(Guid policyId, Guid sourceVersionId)
    {
        var response = await _client.PostAsync(
            $"/api/policies/{policyId}/versions/{sourceVersionId}/bump", content: null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
    }

    [Fact]
    public async Task Publish_OnDraft_Returns200_WithActiveState()
    {
        var draft = await CreateDraftAsync("publish-happy");

        var response = await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            WithRationale());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<PolicyVersionDto>();
        dto!.State.Should().Be("Active");
        dto.Id.Should().Be(draft.Id);
    }

    [Fact]
    public async Task Publish_NewerDraft_AutoSupersedesPreviousActive()
    {
        var v1 = await CreateDraftAsync("auto-supersede");
        await _client.PostAsJsonAsync(
            $"/api/policies/{v1.PolicyId}/versions/{v1.Id}/publish",
            WithRationale("v1-go-live"));

        // Bumping the published v1 mints a fresh Draft (v2) under the same policy.
        var v2 = await BumpDraftAsync(v1.PolicyId, v1.Id);

        var response = await _client.PostAsJsonAsync(
            $"/api/policies/{v2.PolicyId}/versions/{v2.Id}/publish",
            WithRationale("v2-go-live"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var publishedV2 = await response.Content.ReadFromJsonAsync<PolicyVersionDto>();
        publishedV2!.State.Should().Be("Active");

        // Re-read v1 — it must now be WindingDown.
        var v1After = await _client.GetFromJsonAsync<PolicyVersionDto>(
            $"/api/policies/{v1.PolicyId}/versions/{v1.Id}");
        v1After!.State.Should().Be("WindingDown");

        // The active resolution endpoint should now return v2.
        var active = await _client.GetFromJsonAsync<PolicyVersionDto>(
            $"/api/policies/{v1.PolicyId}/versions/active");
        active!.Id.Should().Be(v2.Id);
    }

    [Fact]
    public async Task Publish_WithEmptyRationale_Returns400()
    {
        var draft = await CreateDraftAsync("publish-no-rationale");

        var response = await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest(Rationale: "   "));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be("Validation failed");
    }

    [Fact]
    public async Task Publish_OnUnknownPolicy_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/policies/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/publish",
            WithRationale());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Publish_OnRetiredVersion_Returns409()
    {
        var draft = await CreateDraftAsync("publish-retired-blocked");
        await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            WithRationale("live"));
        // Active -> Retired is allowed and tombstones the version.
        await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/retire",
            WithRationale("tomb"));

        var response = await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            WithRationale("rezz"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be("Invalid lifecycle transition");
    }

    [Fact]
    public async Task WindDown_OnActive_Returns200_AndState()
    {
        var draft = await CreateDraftAsync("wind-down-happy");
        await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            WithRationale("live"));

        var response = await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/winding-down",
            WithRationale("sunset"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<PolicyVersionDto>();
        dto!.State.Should().Be("WindingDown");
    }

    [Fact]
    public async Task WindDown_OnDraft_Returns409()
    {
        // Draft -> WindingDown is not in the matrix.
        var draft = await CreateDraftAsync("wind-down-from-draft");

        var response = await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/winding-down",
            WithRationale("nope"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Retire_FromWindingDown_Returns200_AndStampsState()
    {
        var draft = await CreateDraftAsync("retire-from-winding");
        await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            WithRationale("live"));
        await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/winding-down",
            WithRationale("sunset"));

        var response = await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/retire",
            WithRationale("tomb"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<PolicyVersionDto>();
        dto!.State.Should().Be("Retired");
    }

    [Fact]
    public async Task Retire_OnDraft_Returns409()
    {
        // Draft -> Retired is not in the matrix; only Active and WindingDown can retire.
        var draft = await CreateDraftAsync("retire-from-draft");

        var response = await _client.PostAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/retire",
            WithRationale("nope"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

}
