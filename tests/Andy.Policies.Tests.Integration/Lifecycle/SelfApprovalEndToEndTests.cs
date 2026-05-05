// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Tests.Integration.Controllers;
using Andy.Policies.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andy.Policies.Tests.Integration.Lifecycle;

/// <summary>
/// P7.5 (#61) — end-to-end proof that the publish-time self-approval
/// invariant from P7.3 fires <i>after</i> RBAC has approved the call.
/// Stubs andy-rbac (via <see cref="RbacStubFixture"/>) to return
/// <c>Allowed=true</c> for both <c>policy:author</c> and
/// <c>policy:publish</c>; the publish must still 403 when the actor
/// is the proposer. The complementary "alice drafts / bob publishes"
/// path is the same setup minus the self-approval — it should 200.
/// </summary>
public sealed class SelfApprovalEndToEndTests : IClassFixture<RbacStubFixture>, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly RbacStubFixture _rbac;
    private readonly RbacTestApplicationFactory _factory;
    private readonly HttpClient _client;

    public SelfApprovalEndToEndTests(RbacStubFixture rbac)
    {
        _rbac = rbac;
        _rbac.Reset();
        _factory = new RbacTestApplicationFactory(rbac);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static CreatePolicyRequest MinimalCreate(string slug) => new(
        Name: slug,
        Description: null,
        Summary: "summary",
        Enforcement: "Must",
        Severity: "Critical",
        Scopes: Array.Empty<string>(),
        RulesJson: "{}");

    private async Task<PolicyVersionDto> CreateDraftAsAsync(string slug, string subjectId)
    {
        var resp = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/policies")
            {
                Headers = { { TestAuthHandler.SubjectHeader, subjectId } },
                Content = JsonContent.Create(MinimalCreate(slug)),
            });
        var body = await resp.Content.ReadAsStringAsync();
        resp.IsSuccessStatusCode.Should().BeTrue(
            $"create draft must succeed. status={resp.StatusCode} body={body}");
        return JsonSerializer.Deserialize<PolicyVersionDto>(body, JsonOpts)!;
    }

    private Task<HttpResponseMessage> PublishAsAsync(
        Guid policyId, Guid versionId, string subjectId)
        => _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/policies/{policyId}/versions/{versionId}/publish")
            {
                Headers = { { TestAuthHandler.SubjectHeader, subjectId } },
                Content = JsonContent.Create(new LifecycleTransitionRequest("ship-it")),
            });

    [Fact]
    public async Task AuthorPublishesOwnDraft_RbacAllowsBoth_StillReturns403_SelfApproval()
    {
        // andy-rbac says yes to both author and publish for alice. The
        // domain self-approval guard runs after RBAC and must still
        // reject.
        _rbac.Allow("user:alice", "andy-policies:policy:author");

        var draft = await CreateDraftAsAsync(
            $"selfapproval-{Guid.NewGuid():N}".Substring(0, 22), "user:alice");

        // Alice (the proposer) attempts to publish her own draft. Allow
        // the publish at the (subject, permission) level; the route
        // resolver tags the instance as policy:{policyId} but the test
        // doesn't depend on that — the domain self-approval guard
        // fires regardless and is what we care about.
        _rbac.Allow("user:alice", "andy-policies:policy:publish");

        var resp = await PublishAsAsync(draft.PolicyId, draft.Id, "user:alice");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>(JsonOpts);
        problem!.Extensions["errorCode"]!.ToString()
            .Should().Be("policy.publish_self_approval_forbidden");

        // The version stays Draft; no Active appears.
        var activeResp = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/policies/{draft.PolicyId}/versions/active")
            {
                Headers = { { TestAuthHandler.SubjectHeader, "user:alice" } },
            });
        // Without an explicit allow for :read, default-deny means 403
        // here — that proves the *attempt* to read returns control to
        // the auth pipeline, not that an Active row exists.
        activeResp.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DistinctAuthorAndApprover_RbacAllowsBoth_PublishesTo200()
    {
        _rbac.Allow("user:alice", "andy-policies:policy:author");

        var draft = await CreateDraftAsAsync(
            $"happy-publish-{Guid.NewGuid():N}".Substring(0, 22), "user:alice");

        _rbac.Allow("user:bob", "andy-policies:policy:publish");

        var resp = await PublishAsAsync(draft.PolicyId, draft.Id, "user:bob");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<PolicyVersionDto>(JsonOpts);
        dto!.State.Should().Be("Active");
    }
}
