// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Xunit;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// Integration tests for <c>PoliciesController</c> (P1.5, story
/// rivoli-ai/andy-policies#75). Exercises the REST surface end-to-end against a
/// SQLite-backed factory and verifies the wire contract: status-code mapping
/// (400/404/409/412), ADR 0001 §6 enum casing, and CreatedAtAction location headers.
/// </summary>
public class PoliciesControllerTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    public PoliciesControllerTests(PoliciesApiFactory factory)
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

    [Fact]
    public async Task UpdateDraft_WithStaleExpectedRevision_Returns412()
    {
        // P9 follow-up #194 (2026-05-07): when the client sends an
        // ExpectedRevision that doesn't match the loaded version's
        // Revision, the service throws DbUpdateConcurrencyException
        // which PolicyExceptionHandler maps to 412 Precondition Failed.
        var createResp = await _client.PostAsJsonAsync(
            "/api/policies", MinimalCreate("rev-stale"));
        createResp.EnsureSuccessStatusCode();
        var draft = (await createResp.Content.ReadFromJsonAsync<PolicyVersionDto>())!;

        // Submit an update with a wrong ExpectedRevision (any non-matching uint).
        var stale = new UpdatePolicyVersionRequest(
            Summary: "updated",
            Enforcement: "Must",
            Severity: "Critical",
            Scopes: Array.Empty<string>(),
            RulesJson: "{}",
            Rationale: null,
            ExpectedRevision: draft.Revision + 9999);
        var updateResp = await _client.PutAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}",
            stale);

        Assert.Equal(HttpStatusCode.PreconditionFailed, updateResp.StatusCode);
    }

    [Fact]
    public async Task UpdateDraft_WithMatchingExpectedRevision_Returns200()
    {
        var createResp = await _client.PostAsJsonAsync(
            "/api/policies", MinimalCreate("rev-fresh"));
        createResp.EnsureSuccessStatusCode();
        var draft = (await createResp.Content.ReadFromJsonAsync<PolicyVersionDto>())!;

        var fresh = new UpdatePolicyVersionRequest(
            Summary: "updated",
            Enforcement: "Must",
            Severity: "Critical",
            Scopes: Array.Empty<string>(),
            RulesJson: "{}",
            Rationale: null,
            ExpectedRevision: draft.Revision);
        var updateResp = await _client.PutAsJsonAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}",
            fresh);

        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updated = (await updateResp.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
        Assert.True(updated.Revision > draft.Revision,
            "EF should have bumped Revision on save (manual uint token, not row-version).");
    }

    [Fact]
    public async Task List_ReturnsEmptyArray_WhenNoPolicies()
    {
        var response = await _client.GetAsync("/api/policies?namePrefix=zzzzz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policies = await response.Content.ReadFromJsonAsync<List<PolicyDto>>();
        Assert.NotNull(policies);
        Assert.Empty(policies!);
    }

    [Fact]
    public async Task Create_Returns201_WithLocationHeaderAndVersionOne()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/policies", MinimalCreate("create-flow"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var version = await response.Content.ReadFromJsonAsync<PolicyVersionDto>();
        Assert.NotNull(version);
        Assert.Equal(1, version!.Version);
        Assert.Equal("Draft", version.State);
    }

    [Fact]
    public async Task Create_EmitsAdr0001WireFormatCasing()
    {
        // Submit Pascal/Title-cased values; service should normalise to ADR 0001 §6 casing.
        var response = await _client.PostAsJsonAsync(
            "/api/policies", MinimalCreate("wire-format"));
        response.EnsureSuccessStatusCode();

        // Inspect the raw JSON to confirm the casing on the wire (a typed read would mask
        // a JSON-side mistake — we want to lock the actual byte-level shape).
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("MUST", doc.RootElement.GetProperty("enforcement").GetString());
        Assert.Equal("critical", doc.RootElement.GetProperty("severity").GetString());
        Assert.Equal("Draft", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Get_ReturnsPolicy_AfterCreate()
    {
        var created = await CreateAndUnwrap("get-flow");

        var response = await _client.GetAsync($"/api/policies/{created.PolicyId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var policy = await response.Content.ReadFromJsonAsync<PolicyDto>();
        Assert.Equal("get-flow", policy!.Name);
        Assert.Equal(1, policy.VersionCount);
        Assert.Null(policy.ActiveVersionId); // still Draft
    }

    [Fact]
    public async Task Get_Returns404_WhenMissing()
    {
        var response = await _client.GetAsync($"/api/policies/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetByName_ReturnsPolicy()
    {
        await CreateAndUnwrap("by-name-flow");

        var response = await _client.GetAsync("/api/policies/by-name/by-name-flow");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetByName_Returns404_WhenMissing()
    {
        var response = await _client.GetAsync("/api/policies/by-name/no-such-policy");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListVersions_ReturnsSingleEntry_AfterCreate()
    {
        var created = await CreateAndUnwrap("list-versions");

        var response = await _client.GetAsync($"/api/policies/{created.PolicyId}/versions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var versions = await response.Content.ReadFromJsonAsync<List<PolicyVersionDto>>();
        Assert.Single(versions!);
        Assert.Equal(1, versions![0].Version);
    }

    [Fact]
    public async Task GetVersion_ReturnsVersion_AfterCreate()
    {
        var created = await CreateAndUnwrap("get-version");

        var response = await _client.GetAsync(
            $"/api/policies/{created.PolicyId}/versions/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetActiveVersion_Returns404_WhenAllDraft()
    {
        var created = await CreateAndUnwrap("active-when-draft");

        var response = await _client.GetAsync(
            $"/api/policies/{created.PolicyId}/versions/active");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithDuplicateSlug_Returns409()
    {
        await CreateAndUnwrap("dup-slug");

        var response = await _client.PostAsJsonAsync(
            "/api/policies", MinimalCreate("dup-slug"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithInvalidName_Returns400()
    {
        var bad = MinimalCreate("BadSlug"); // uppercase rejected by ADR 0001 §1 regex
        var response = await _client.PostAsJsonAsync("/api/policies", bad);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithWildcardScope_Returns400()
    {
        var bad = MinimalCreate("wildcard-scope") with { Scopes = new[] { "*" } };
        var response = await _client.PostAsJsonAsync("/api/policies", bad);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateDraft_Returns200_WithMutations()
    {
        var created = await CreateAndUnwrap("update-flow");

        var update = new UpdatePolicyVersionRequest(
            Summary: "revised",
            Enforcement: "should",
            Severity: "moderate",
            Scopes: new[] { "staging" },
            RulesJson: "{\"allow\":true}");

        var response = await _client.PutAsJsonAsync(
            $"/api/policies/{created.PolicyId}/versions/{created.Id}", update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<PolicyVersionDto>();
        Assert.Equal("revised", updated!.Summary);
        Assert.Equal("SHOULD", updated.Enforcement);
        Assert.Equal("moderate", updated.Severity);
    }

    [Fact]
    public async Task UpdateDraft_OnMissingVersion_Returns404()
    {
        var update = new UpdatePolicyVersionRequest(
            "x", "must", "critical", Array.Empty<string>(), "{}");
        var response = await _client.PutAsJsonAsync(
            $"/api/policies/{Guid.NewGuid()}/versions/{Guid.NewGuid()}", update);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Bump_OnOpenDraft_Returns409()
    {
        // Cannot bump while a Draft is still open (ADR 0001 §4).
        var created = await CreateAndUnwrap("bump-blocked");

        var response = await _client.PostAsync(
            $"/api/policies/{created.PolicyId}/versions/{created.Id}/bump", content: null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Bump_OnMissingPolicy_Returns404()
    {
        var response = await _client.PostAsync(
            $"/api/policies/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/bump", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_PersistsCanonicalisedScopes()
    {
        var req = MinimalCreate("scope-canonical") with
        {
            Scopes = new[] { "tool:z", "tool:a", "prod" }
        };
        var response = await _client.PostAsJsonAsync("/api/policies", req);
        response.EnsureSuccessStatusCode();

        var version = await response.Content.ReadFromJsonAsync<PolicyVersionDto>();
        Assert.Equal(new[] { "prod", "tool:a", "tool:z" }, version!.Scopes);
    }

    private async Task<PolicyVersionDto> CreateAndUnwrap(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/policies", MinimalCreate(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
    }
}
