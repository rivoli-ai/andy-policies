// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Lifecycle;

/// <summary>
/// #216 — REST surface for the Propose / Reject draft-handoff
/// endpoints (P9.3 backend prereq). Pins the wire contract: state
/// stays Draft across both transitions, ReadyForReview flips
/// accordingly, and the pending-approval inbox reflects the flag.
/// </summary>
public class ProposeRejectTests : IClassFixture<PoliciesApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;

    public ProposeRejectTests(PoliciesApiFactory factory)
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

    private static string Slug(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}".Substring(0, Math.Min(24, prefix.Length + 17));

    private async Task<PolicyVersionDto> CreateDraftAsync(string slug, string subjectId)
    {
        var resp = await _client.PostAsJsonAsSubjectAsync(
            "/api/policies", MinimalCreate(slug), subjectId);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PolicyVersionDto>(JsonOptions))!;
    }

    [Fact]
    public async Task Propose_FlipsReadyForReview_AndStaysInDraft()
    {
        var draft = await CreateDraftAsync(Slug("propose"), "user:author");
        draft.ReadyForReview.Should().BeFalse();

        var resp = await _client.PostAsJsonAsSubjectAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/propose",
            new LifecycleTransitionRequest("ready for review"),
            "user:author");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<PolicyVersionDto>(JsonOptions))!;
        dto.ReadyForReview.Should().BeTrue();
        dto.State.Should().Be("Draft",
            "propose is a soft handoff — it does not transition the lifecycle state");
    }

    [Fact]
    public async Task Reject_ClearsReadyForReview_AndRequiresRationale()
    {
        var draft = await CreateDraftAsync(Slug("reject"), "user:author");
        await _client.PostAsJsonAsSubjectAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/propose",
            new LifecycleTransitionRequest("looks good"),
            "user:author");

        // Empty rationale: 400 — audit chain needs the reason.
        var blank = await _client.PostAsJsonAsApproverAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/reject",
            new LifecycleTransitionRequest(""));
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var resp = await _client.PostAsJsonAsApproverAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/reject",
            new LifecycleTransitionRequest("missing test coverage"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<PolicyVersionDto>(JsonOptions))!;
        dto.ReadyForReview.Should().BeFalse();
        dto.State.Should().Be("Draft",
            "option (a) reject semantics: revert to plain Draft, no terminal state");
    }

    [Fact]
    public async Task PendingApproval_ReturnsOnlyProposedDrafts()
    {
        // Three drafts: proposed, untouched, proposed-then-rejected.
        var d1 = await CreateDraftAsync(Slug("inbox-1"), "user:author");
        var d2 = await CreateDraftAsync(Slug("inbox-2"), "user:author");
        var d3 = await CreateDraftAsync(Slug("inbox-3"), "user:author");

        await _client.PostAsJsonAsSubjectAsync(
            $"/api/policies/{d1.PolicyId}/versions/{d1.Id}/propose",
            new LifecycleTransitionRequest("r1"), "user:author");
        await _client.PostAsJsonAsSubjectAsync(
            $"/api/policies/{d3.PolicyId}/versions/{d3.Id}/propose",
            new LifecycleTransitionRequest("r3"), "user:author");
        await _client.PostAsJsonAsApproverAsync(
            $"/api/policies/{d3.PolicyId}/versions/{d3.Id}/reject",
            new LifecycleTransitionRequest("changed mind"));

        var resp = await _client.GetAsync("/api/policies/pending-approval");
        resp.EnsureSuccessStatusCode();
        var rows = await resp.Content.ReadFromJsonAsync<List<PolicyVersionDto>>(JsonOptions);

        rows.Should().NotBeNull();
        var ids = rows!.Select(r => r.Id).ToList();
        ids.Should().Contain(d1.Id);
        ids.Should().NotContain(d2.Id, "d2 was never proposed");
        ids.Should().NotContain(d3.Id, "d3 was rejected back to plain Draft");
    }

    [Fact]
    public async Task Propose_OnNonDraftVersion_Returns409()
    {
        var draft = await CreateDraftAsync(Slug("notdraft"), "user:author");
        var publishResp = await _client.PostAsJsonAsApproverAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest("go-live"));
        publishResp.EnsureSuccessStatusCode();

        var resp = await _client.PostAsJsonAsSubjectAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/propose",
            new LifecycleTransitionRequest("late"),
            "user:author");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
