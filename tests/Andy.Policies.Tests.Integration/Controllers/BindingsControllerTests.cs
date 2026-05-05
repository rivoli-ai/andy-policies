// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// Integration tests for the binding REST surface (P3.3, story
/// rivoli-ai/andy-policies#21). Exercises every route end-to-end against
/// the SQLite-backed factory: create round-trip, retired-version refusal,
/// soft-delete tombstone semantics, exact-match target query, and the
/// version-rooted enumeration.
/// </summary>
public class BindingsControllerTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    /// <summary>
    /// API serializes enums as strings via the global
    /// <see cref="JsonStringEnumConverter"/> registered in Program.cs.
    /// Tests need the same converter to deserialize <see cref="BindingDto"/>
    /// back into <see cref="BindingTargetType"/> / <see cref="BindStrength"/>.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public BindingsControllerTests(PoliciesApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static CreatePolicyRequest MinimalCreatePolicy(string name) => new(
        Name: name,
        Description: null,
        Summary: "summary",
        Enforcement: "Must",
        Severity: "Critical",
        Scopes: Array.Empty<string>(),
        RulesJson: "{}");

    private async Task<PolicyVersionDto> CreateDraftAsync(string slug)
    {
        var resp = await _client.PostAsJsonAsync("/api/policies", MinimalCreatePolicy(slug));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
    }

    private async Task<PolicyVersionDto> PublishAsync(PolicyVersionDto draft)
    {
        var resp = await _client.PostAsJsonAsApproverAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest("ship-it"));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
    }

    private static CreateBindingRequest BindingFor(Guid versionId, string targetRef = "repo:rivoli-ai/policy-x")
        => new(versionId, BindingTargetType.Repo, targetRef, BindStrength.Mandatory);

    private static string Slug(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}".Substring(0, 16);

    private async Task<List<BindingDto>?> GetListAsync(string url)
    {
        var resp = await _client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<BindingDto>>(JsonOptions);
    }

    [Fact]
    public async Task Create_Returns201_WithLocationHeaderAndDto()
    {
        var draft = await CreateDraftAsync(Slug("bind-create"));

        var resp = await _client.PostAsJsonAsync("/api/bindings", BindingFor(draft.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        resp.Headers.Location.Should().NotBeNull();
        resp.Headers.Location!.AbsolutePath.Should().StartWith("/api/bindings/");

        var dto = await resp.Content.ReadFromJsonAsync<BindingDto>(JsonOptions);
        dto!.PolicyVersionId.Should().Be(draft.Id);
        dto.TargetType.Should().Be(BindingTargetType.Repo);
        dto.BindStrength.Should().Be(BindStrength.Mandatory);
        dto.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Create_OnRetiredVersion_Returns409()
    {
        var draft = await CreateDraftAsync(Slug("bind-retired"));
        var active = await PublishAsync(draft);
        // Active -> Retired is a valid transition (emergency recall path).
        var retire = await _client.PostAsJsonAsync(
            $"/api/policies/{active.PolicyId}/versions/{active.Id}/retire",
            new LifecycleTransitionRequest("recall"));
        retire.EnsureSuccessStatusCode();

        var resp = await _client.PostAsJsonAsync("/api/bindings", BindingFor(active.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_OnUnknownVersion_Returns404()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/bindings", BindingFor(Guid.NewGuid()));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_WithEmptyTargetRef_Returns400()
    {
        var draft = await CreateDraftAsync(Slug("bind-empty"));

        var resp = await _client.PostAsJsonAsync(
            "/api/bindings", BindingFor(draft.Id, targetRef: "  "));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_RoundTripsAfterCreate_AndReturns404ForUnknownId()
    {
        var draft = await CreateDraftAsync(Slug("bind-get"));
        var created = await _client.PostAsJsonAsync("/api/bindings", BindingFor(draft.Id));
        created.EnsureSuccessStatusCode();
        var dto = (await created.Content.ReadFromJsonAsync<BindingDto>(JsonOptions))!;

        var ok = await _client.GetAsync($"/api/bindings/{dto.Id}");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var reloaded = await ok.Content.ReadFromJsonAsync<BindingDto>(JsonOptions);
        reloaded!.Id.Should().Be(dto.Id);

        var missing = await _client.GetAsync($"/api/bindings/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_Returns204_AndSecondDeleteReturns404()
    {
        var draft = await CreateDraftAsync(Slug("bind-del"));
        var created = await _client.PostAsJsonAsync("/api/bindings", BindingFor(draft.Id));
        var dto = (await created.Content.ReadFromJsonAsync<BindingDto>(JsonOptions))!;

        var first = await _client.DeleteAsync($"/api/bindings/{dto.Id}?rationale=no-longer-needed");
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Tombstoned rows are treated as not-found by the service contract.
        var second = await _client.DeleteAsync($"/api/bindings/{dto.Id}");
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Query_ByTarget_ReturnsExactMatchOnly()
    {
        var draft = await CreateDraftAsync(Slug("bind-q"));
        var lower = $"repo:rivoli-ai/q-{Guid.NewGuid():N}".Substring(0, 30);
        var upper = lower.ToUpperInvariant();
        await _client.PostAsJsonAsync("/api/bindings", BindingFor(draft.Id, lower));
        await _client.PostAsJsonAsync("/api/bindings", BindingFor(draft.Id, upper));

        var lowerResp = await GetListAsync(
            $"/api/bindings?targetType=Repo&targetRef={Uri.EscapeDataString(lower)}");
        lowerResp.Should().ContainSingle().Which.TargetRef.Should().Be(lower);

        var upperResp = await GetListAsync(
            $"/api/bindings?targetType=Repo&targetRef={Uri.EscapeDataString(upper)}");
        upperResp.Should().ContainSingle().Which.TargetRef.Should().Be(upper);
    }

    [Fact]
    public async Task Query_WithMissingTargetRef_Returns400()
    {
        var resp = await _client.GetAsync("/api/bindings?targetType=Repo");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListByPolicyVersion_HonoursIncludeDeletedFlag()
    {
        var draft = await CreateDraftAsync(Slug("bind-list"));
        var aliveResp = await _client.PostAsJsonAsync("/api/bindings", BindingFor(draft.Id, "repo:a/alive"));
        var deadResp = await _client.PostAsJsonAsync("/api/bindings", BindingFor(draft.Id, "repo:a/dead"));
        var dead = (await deadResp.Content.ReadFromJsonAsync<BindingDto>(JsonOptions))!;
        await _client.DeleteAsync($"/api/bindings/{dead.Id}");

        var visible = await GetListAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/bindings?includeDeleted=false");
        visible.Should().ContainSingle();
        visible![0].DeletedAt.Should().BeNull();

        var all = await GetListAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/bindings?includeDeleted=true");
        all.Should().HaveCount(2);
    }
}
