// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andy.Policies.Tests.Integration.Lifecycle;

/// <summary>
/// P7.3 (#55) — verifies the publish-time self-approval guard at the
/// REST surface. Asserts the 403 response shape (typed errorCode,
/// matched extensions) and the "no state mutation on reject" contract
/// (the version stays Draft and no Active appears under the policy).
/// </summary>
public class PublishSelfApprovalTests : IClassFixture<PoliciesApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;

    public PublishSelfApprovalTests(PoliciesApiFactory factory)
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

    private async Task<PolicyVersionDto> CreateDraftAsAsync(string slug, string subjectId)
    {
        var resp = await _client.PostAsJsonAsSubjectAsync(
            "/api/policies", MinimalCreate(slug), subjectId);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PolicyVersionDto>(JsonOptions))!;
    }

    [Fact]
    public async Task Publish_BySameSubjectAsProposer_Returns403_AndDoesNotMutate()
    {
        var draft = await CreateDraftAsAsync(
            $"selfapproval-blocked-{Guid.NewGuid():N}".Substring(0, 24), "user:alice");
        draft.ProposerSubjectId.Should().Be("user:alice");

        var resp = await _client.PostAsJsonAsSubjectAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest("self-publish"),
            "user:alice");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        problem!.Title.Should().Be("Self-approval forbidden");
        problem.Type.Should().Be("/problems/publish-self-approval");
        problem.Extensions["errorCode"]!.ToString().Should().Be("policy.publish_self_approval_forbidden");

        // No Active version exists for the policy after the failed publish.
        var activeResp = await _client.GetAsync(
            $"/api/policies/{draft.PolicyId}/versions/active");
        activeResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The draft itself remains in Draft state.
        var stillDraft = await _client.GetFromJsonAsync<PolicyVersionDto>(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}", JsonOptions);
        stillDraft!.State.Should().Be("Draft");
    }

    [Fact]
    public async Task Publish_ByDifferentSubject_Returns200_AndFlipsToActive()
    {
        var draft = await CreateDraftAsAsync(
            $"selfapproval-allowed-{Guid.NewGuid():N}".Substring(0, 24), "user:alice");

        var resp = await _client.PostAsJsonAsSubjectAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest("ship-it"),
            "user:bob");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<PolicyVersionDto>(JsonOptions);
        dto!.State.Should().Be("Active");
    }
}
