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
/// Integration tests for the scope REST surface (P4.5, story
/// rivoli-ai/andy-policies#33). Drives every endpoint end-to-end
/// against the SQLite-backed factory: full CRUD round-trip, tree
/// shape, effective-policies bridging to P4.3, the 400 / 409
/// problem-details payloads with their typed errorCodes, and the
/// type-ladder enforcement.
/// </summary>
public class ScopesControllerTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public ScopesControllerTests(PoliciesApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<ScopeNodeDto> CreateAsync(Guid? parentId, ScopeType type, string @ref, string display)
    {
        var resp = await _client.PostAsJsonAsync("/api/scopes",
            new { parentId, type = type.ToString(), @ref, displayName = display });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ScopeNodeDto>(JsonOptions))!;
    }

    private static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}".Substring(0, 20);

    [Fact]
    public async Task FullCrudRoundTrip_RootOrgPlusChildTenant_TreeAndDeleteAreCorrect()
    {
        var orgRef = Slug("org:e2e");
        var tenantRef = Slug("tenant:e2e");

        // Create root.
        var org = await CreateAsync(null, ScopeType.Org, orgRef, "E2E Org");
        org.ParentId.Should().BeNull();
        org.Depth.Should().Be(0);

        // Create child.
        var tenant = await CreateAsync(org.Id, ScopeType.Tenant, tenantRef, "E2E Tenant");
        tenant.ParentId.Should().Be(org.Id);
        tenant.Depth.Should().Be(1);

        // GET /api/scopes/{id} returns the row we just created.
        var fetched = await _client.GetFromJsonAsync<ScopeNodeDto>(
            $"/api/scopes/{tenant.Id}", JsonOptions);
        fetched!.Id.Should().Be(tenant.Id);

        // GET /api/scopes/tree returns a forest. Find our org and confirm it
        // has a child whose id matches our tenant.
        var forest = await _client.GetFromJsonAsync<List<ScopeTreeDto>>(
            "/api/scopes/tree", JsonOptions);
        var orgTree = forest!.FirstOrDefault(t => t.Node.Id == org.Id);
        orgTree.Should().NotBeNull();
        orgTree!.Children.Select(c => c.Node.Id).Should().Contain(tenant.Id);

        // DELETE root → 409 (has descendants), 204 once child is gone.
        var rootDeleteAttempt = await _client.DeleteAsync($"/api/scopes/{org.Id}");
        rootDeleteAttempt.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var childDelete = await _client.DeleteAsync($"/api/scopes/{tenant.Id}");
        childDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rootDelete = await _client.DeleteAsync($"/api/scopes/{org.Id}");
        rootDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Get_OnUnknownId_Returns404()
    {
        var resp = await _client.GetAsync($"/api/scopes/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_WithTeamAndNoParent_Returns400_WithParentTypeMismatchCode()
    {
        var resp = await _client.PostAsJsonAsync("/api/scopes",
            new { parentId = (Guid?)null, type = "Team", @ref = Slug("team:bad"), displayName = "Bad" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("scope.parent-type-mismatch");
        doc.RootElement.GetProperty("type").GetString().Should().Be("/problems/scope-parent-type-mismatch");
    }

    [Fact]
    public async Task Create_WithTeamUnderOrg_Returns400_WithParentTypeMismatchCode()
    {
        var org = await CreateAsync(null, ScopeType.Org, Slug("org:lo"), "Org");

        var resp = await _client.PostAsJsonAsync("/api/scopes",
            new { parentId = org.Id, type = "Team", @ref = Slug("team:wrong"), displayName = "Team" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("scope.parent-type-mismatch");
    }

    [Fact]
    public async Task Create_WithDuplicateTypeRef_Returns409_WithRefConflictCode()
    {
        var refValue = Slug("org:dup");
        await CreateAsync(null, ScopeType.Org, refValue, "First");

        var resp = await _client.PostAsJsonAsync("/api/scopes",
            new { parentId = (Guid?)null, type = "Org", @ref = refValue, displayName = "Second" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("scope.ref-conflict");
        doc.RootElement.GetProperty("scopeType").GetString().Should().Be("Org");
        doc.RootElement.GetProperty("ref").GetString().Should().Be(refValue);
    }

    [Fact]
    public async Task Delete_OnNonLeaf_Returns409_WithChildCount()
    {
        var org = await CreateAsync(null, ScopeType.Org, Slug("org:nonleaf"), "Org");
        await CreateAsync(org.Id, ScopeType.Tenant, Slug("tenant:nonleaf"), "Tenant");

        var resp = await _client.DeleteAsync($"/api/scopes/{org.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("errorCode").GetString().Should().Be("scope.has-descendants");
        doc.RootElement.GetProperty("scopeNodeId").GetGuid().Should().Be(org.Id);
        doc.RootElement.GetProperty("childCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Create_WithMissingParent_Returns404()
    {
        var resp = await _client.PostAsJsonAsync("/api/scopes",
            new { parentId = Guid.NewGuid(), type = "Tenant", @ref = Slug("tenant:orphan"), displayName = "Orphan" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_WithTypeFilter_ReturnsOnlyMatching()
    {
        var org = await CreateAsync(null, ScopeType.Org, Slug("org:list"), "Org");
        await CreateAsync(org.Id, ScopeType.Tenant, Slug("tenant:list"), "Tenant");

        var resp = await _client.GetFromJsonAsync<List<ScopeNodeDto>>(
            "/api/scopes?type=Tenant", JsonOptions);

        resp.Should().NotBeNull().And.NotBeEmpty();
        resp!.All(n => n.Type == ScopeType.Tenant).Should().BeTrue();
    }

    [Fact]
    public async Task Effective_ReturnsEnvelopeWithScopeNodeIdAndPolicies()
    {
        // The resolution semantics are exercised exhaustively in
        // BindingResolutionServiceTests; this assertion just confirms
        // the REST surface forwards correctly and returns 200 with the
        // expected envelope shape.
        var org = await CreateAsync(null, ScopeType.Org, Slug("org:eff"), "Org");

        var resp = await _client.GetFromJsonAsync<EffectivePolicySetDto>(
            $"/api/scopes/{org.Id}/effective-policies", JsonOptions);

        resp.Should().NotBeNull();
        resp!.ScopeNodeId.Should().Be(org.Id);
        resp.Policies.Should().BeEmpty("no bindings seeded against this org");
    }

    [Fact]
    public async Task Effective_OnUnknownScope_Returns404()
    {
        var resp = await _client.GetAsync($"/api/scopes/{Guid.NewGuid()}/effective-policies");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_Returns201_WithLocationHeader()
    {
        var resp = await _client.PostAsJsonAsync("/api/scopes",
            new { parentId = (Guid?)null, type = "Org", @ref = Slug("org:loc"), displayName = "Loc" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        resp.Headers.Location.Should().NotBeNull();
        resp.Headers.Location!.AbsolutePath.Should().StartWith("/api/scopes/");
    }
}
