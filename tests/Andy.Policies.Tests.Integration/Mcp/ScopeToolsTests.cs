// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Api.Mcp;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Andy.Policies.Tests.Integration.Mcp;

/// <summary>
/// Tests for <see cref="ScopeTools"/> (P4.6, story
/// rivoli-ai/andy-policies#34). Drives the static tool methods
/// directly against a real <see cref="ScopeService"/> +
/// <see cref="BindingResolutionService"/> backed by EF Core
/// InMemory. Verifies the wire contract: formatted strings on
/// success, prefixed error codes on failure
/// (<c>policy.scope.{not_found,parent_type_mismatch,ref_conflict,has_descendants,invalid_input}</c>),
/// JSON envelopes on tree + effective.
/// </summary>
public class ScopeToolsTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static (ScopeService scopes, BindingResolutionService resolver, AppDbContext db) NewServices()
    {
        var db = NewDb();
        var scopes = new ScopeService(db, TimeProvider.System);
        var resolver = new BindingResolutionService(db, scopes);
        return (scopes, resolver, db);
    }

    [Fact]
    public async Task List_OnEmptyDb_ReturnsHelpfulMessage()
    {
        var (scopes, _, _) = NewServices();

        var output = await ScopeTools.List(scopes);

        output.Should().Contain("No scope nodes");
    }

    [Fact]
    public async Task List_WithTypeFilter_FormatsHeader_AndOneLinePerNode()
    {
        var (scopes, _, _) = NewServices();
        await scopes.CreateAsync(new CreateScopeNodeRequest(null, ScopeType.Org, "org:t-list", "T"));

        var output = await ScopeTools.List(scopes, type: "Org");

        output.Should().Contain("1 scope node:");
        output.Should().Contain("[Org@0]");
        output.Should().Contain("org:t-list");
    }

    [Fact]
    public async Task List_WithInvalidType_ReturnsInvalidInputError()
    {
        var (scopes, _, _) = NewServices();

        var output = await ScopeTools.List(scopes, type: "Unicorn");

        output.Should().StartWith("policy.scope.invalid_input:");
    }

    [Fact]
    public async Task Get_RoundTripsAfterCreate_AndReturnsNotFoundForUnknownId()
    {
        var (scopes, _, _) = NewServices();
        var dto = await scopes.CreateAsync(new CreateScopeNodeRequest(null, ScopeType.Org, "org:t-get", "T"));

        var hit = await ScopeTools.Get(scopes, dto.Id.ToString());
        var miss = await ScopeTools.Get(scopes, Guid.NewGuid().ToString());

        hit.Should().Contain($"ScopeNode {dto.Id}");
        miss.Should().StartWith("policy.scope.not_found:");
    }

    [Fact]
    public async Task Get_OnInvalidGuid_ReturnsInvalidInput()
    {
        var (scopes, _, _) = NewServices();

        var output = await ScopeTools.Get(scopes, "not-a-guid");

        output.Should().StartWith("policy.scope.invalid_input:");
    }

    [Fact]
    public async Task Tree_ReturnsValidJsonForest()
    {
        var (scopes, _, _) = NewServices();
        var org = await scopes.CreateAsync(new CreateScopeNodeRequest(null, ScopeType.Org, "org:t-tree", "T"));
        await scopes.CreateAsync(new CreateScopeNodeRequest(org.Id, ScopeType.Tenant, "tenant:t-tree", "Tn"));

        var output = await ScopeTools.Tree(scopes);

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.GetArrayLength().Should().Be(1);
        var root = doc.RootElement[0];
        root.GetProperty("node").GetProperty("id").GetString().Should().Be(org.Id.ToString());
        root.GetProperty("children").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Create_HappyPath_ReturnsFormattedDetail_AndPersists()
    {
        var (scopes, _, db) = NewServices();
        var refValue = $"org:t-create-{Guid.NewGuid():N}".Substring(0, 18);

        var output = await ScopeTools.Create(
            scopes, parentId: null, type: "Org", @ref: refValue, displayName: "Display");

        output.Should().Contain("ScopeNode ");
        output.Should().Contain($"Ref: {refValue}");
        var rows = await db.ScopeNodes.AsNoTracking().Where(s => s.Ref == refValue).ToListAsync();
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_WithLadderViolation_ReturnsParentTypeMismatchCode()
    {
        var (scopes, _, _) = NewServices();
        var org = await scopes.CreateAsync(new CreateScopeNodeRequest(null, ScopeType.Org, "org:t-mis", "Org"));

        var output = await ScopeTools.Create(
            scopes,
            parentId: org.Id.ToString(),
            type: "Team",  // Team's parent must be Tenant, not Org.
            @ref: "team:wrong",
            displayName: "Bad");

        output.Should().StartWith("policy.scope.parent_type_mismatch:");
    }

    [Fact]
    public async Task Create_WithMissingParent_ReturnsNotFoundCode()
    {
        var (scopes, _, _) = NewServices();

        var output = await ScopeTools.Create(
            scopes,
            parentId: Guid.NewGuid().ToString(),
            type: "Tenant",
            @ref: "tenant:orphan",
            displayName: "Orphan");

        output.Should().StartWith("policy.scope.not_found:");
    }

    [Fact]
    public async Task Create_WithInvalidGuidParent_ReturnsInvalidInput()
    {
        var (scopes, _, _) = NewServices();

        var output = await ScopeTools.Create(
            scopes, parentId: "not-a-guid", type: "Tenant", @ref: "tenant:bad", displayName: "Bad");

        output.Should().StartWith("policy.scope.invalid_input:");
    }

    [Fact]
    public async Task Delete_OnLeaf_Succeeds_AndReturnsNotFoundOnSecondCall()
    {
        var (scopes, _, _) = NewServices();
        var dto = await scopes.CreateAsync(new CreateScopeNodeRequest(null, ScopeType.Org, "org:t-del", "Del"));

        var first = await ScopeTools.Delete(scopes, dto.Id.ToString());
        first.Should().Contain("deleted");

        var second = await ScopeTools.Delete(scopes, dto.Id.ToString());
        second.Should().StartWith("policy.scope.not_found:");
    }

    [Fact]
    public async Task Delete_OnNonLeaf_ReturnsHasDescendantsCode_WithChildCount()
    {
        var (scopes, _, _) = NewServices();
        var org = await scopes.CreateAsync(new CreateScopeNodeRequest(null, ScopeType.Org, "org:t-d-non", "Org"));
        await scopes.CreateAsync(new CreateScopeNodeRequest(org.Id, ScopeType.Tenant, "tenant:t-d-non", "Tn"));

        var output = await ScopeTools.Delete(scopes, org.Id.ToString());

        output.Should().StartWith("policy.scope.has_descendants:");
        output.Should().Contain("childCount=1");
    }

    [Fact]
    public async Task Effective_ReturnsValidJsonEnvelope_OnEmptyChain()
    {
        var (scopes, resolver, _) = NewServices();
        var dto = await scopes.CreateAsync(new CreateScopeNodeRequest(null, ScopeType.Org, "org:t-eff", "Eff"));

        var output = await ScopeTools.Effective(resolver, dto.Id.ToString());

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("scopeNodeId").GetString().Should().Be(dto.Id.ToString());
        doc.RootElement.GetProperty("policies").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Effective_OnUnknownId_ReturnsNotFoundCode()
    {
        var (_, resolver, _) = NewServices();

        var output = await ScopeTools.Effective(resolver, Guid.NewGuid().ToString());

        output.Should().StartWith("policy.scope.not_found:");
    }
}
