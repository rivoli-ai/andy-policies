// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Mcp;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Andy.Policies.Tests.Integration.Mcp;

/// <summary>
/// Unit tests for the MCP read tools (P1.6, story rivoli-ai/andy-policies#76).
/// Exercises the static tool methods directly against a real
/// <see cref="PolicyService"/> backed by EF Core InMemory — proves both the
/// formatter logic and the service delegation. The MCP transport itself is
/// provided by the ModelContextProtocol library and is out of scope here;
/// transport-level coverage lands with the cross-surface parity sweep
/// (P1.11, #91).
/// </summary>
public class PolicyToolsTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static CreatePolicyRequest MinimalCreate(string name, string? scope = null) => new(
        Name: name,
        Description: null,
        Summary: "summary",
        Enforcement: "Must",
        Severity: "Critical",
        Scopes: scope is null ? Array.Empty<string>() : new[] { scope },
        RulesJson: "{}");

    [Fact]
    public async Task ListPolicies_NoMatches_ReturnsHelpfulEmptyMessage()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);

        var output = await PolicyTools.ListPolicies(service, namePrefix: "no-such-");

        Assert.Contains("No policies found", output);
    }

    [Fact]
    public async Task ListPolicies_FormatsSummaryLine_PerPolicy()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        await service.CreateDraftAsync(MinimalCreate("first"), "sam");
        await service.CreateDraftAsync(MinimalCreate("second"), "sam");

        var output = await PolicyTools.ListPolicies(service);

        Assert.Contains("2 policies:", output);
        Assert.Contains("first", output);
        Assert.Contains("second", output);
        Assert.Contains("1 version", output);
        Assert.Contains("no active version", output);
    }

    [Fact]
    public async Task ListPolicies_PassesFiltersThrough_ToService()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var a = await service.CreateDraftAsync(MinimalCreate("filtered", scope: "prod"), "sam");
        await service.CreateDraftAsync(MinimalCreate("ignored", scope: "staging"), "sam");
        var entity = await db.PolicyVersions.FirstAsync(v => v.Id == a.Id);
        entity.State = LifecycleState.Active;
        await db.SaveChangesAsync();

        var output = await PolicyTools.ListPolicies(service, scope: "prod");

        Assert.Contains("filtered", output);
        Assert.DoesNotContain("ignored", output);
    }

    [Fact]
    public async Task GetPolicy_InvalidGuid_ReturnsValidationMessage_WithoutCallingService()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);

        var output = await PolicyTools.GetPolicy(service, policyId: "not-a-guid");

        Assert.Contains("not a valid GUID", output);
    }

    [Fact]
    public async Task GetPolicy_NotFound_ReturnsHelpfulMessage()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var missing = Guid.NewGuid();

        var output = await PolicyTools.GetPolicy(service, missing.ToString());

        Assert.Contains($"Policy {missing} not found", output);
    }

    [Fact]
    public async Task GetPolicy_Found_FormatsDetail()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var v = await service.CreateDraftAsync(MinimalCreate("detail-policy"), "sam");

        var output = await PolicyTools.GetPolicy(service, v.PolicyId.ToString());

        Assert.Contains("Policy: detail-policy", output);
        Assert.Contains("Versions: 1", output);
        Assert.Contains("(none — all drafts)", output);
    }

    [Fact]
    public async Task ListVersions_ReturnsDescendingOrder_WithStateAndDimensions()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var v1 = await service.CreateDraftAsync(MinimalCreate("multi-version"), "sam");
        var entity = await db.PolicyVersions.FirstAsync(x => x.Id == v1.Id);
        entity.State = LifecycleState.Active;
        await db.SaveChangesAsync();
        var v2 = await service.BumpDraftFromVersionAsync(v1.PolicyId, v1.Id, "alice");

        var output = await PolicyTools.ListVersions(service, v1.PolicyId.ToString());

        Assert.Contains("2 versions", output);
        // v2 (Draft) appears before v1 (Active) — descending order.
        var v2Index = output.IndexOf("v2 (");
        var v1Index = output.IndexOf("v1 (");
        Assert.True(v2Index > -1 && v1Index > v2Index, "Expected v2 before v1 (descending)");
        Assert.Contains("Draft", output);
        Assert.Contains("Active", output);
        Assert.Contains("MUST/critical", output);
    }

    [Fact]
    public async Task ListVersions_NoSuchPolicy_ReturnsHelpfulEmptyMessage()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);

        var output = await PolicyTools.ListVersions(service, Guid.NewGuid().ToString());

        Assert.Contains("No versions found", output);
    }

    [Fact]
    public async Task GetVersion_Found_FormatsRulesJsonAndScopes()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var v = await service.CreateDraftAsync(
            MinimalCreate("rich-version", scope: "prod") with { RulesJson = "{\"allow\":true}" },
            "sam");

        var output = await PolicyTools.GetVersion(service, v.PolicyId.ToString(), v.Id.ToString());

        Assert.Contains($"v{v.Version} of policy {v.PolicyId}", output);
        Assert.Contains("State: Draft", output);
        Assert.Contains("Enforcement: MUST", output);
        Assert.Contains("Severity: critical", output);
        Assert.Contains("Scopes: prod", output);
        Assert.Contains("{\"allow\":true}", output);
    }

    [Fact]
    public async Task GetVersion_NotFound_ReturnsHelpfulMessage()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var missingPolicy = Guid.NewGuid();
        var missingVersion = Guid.NewGuid();

        var output = await PolicyTools.GetVersion(
            service, missingPolicy.ToString(), missingVersion.ToString());

        Assert.Contains("not found", output);
    }

    [Fact]
    public async Task GetActiveVersion_AllDraft_ReturnsHelpfulMessage()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var v = await service.CreateDraftAsync(MinimalCreate("only-draft"), "sam");

        var output = await PolicyTools.GetActiveVersion(service, v.PolicyId.ToString());

        Assert.Contains("no active version", output);
    }

    [Fact]
    public async Task GetActiveVersion_AfterTransition_ResolvesAndFormatsDetail()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var v = await service.CreateDraftAsync(MinimalCreate("active-flow"), "sam");
        var entity = await db.PolicyVersions.FirstAsync(x => x.Id == v.Id);
        entity.State = LifecycleState.Active;
        await db.SaveChangesAsync();

        var output = await PolicyTools.GetActiveVersion(service, v.PolicyId.ToString());

        Assert.Contains($"v{v.Version} of policy {v.PolicyId}", output);
        Assert.Contains("State: Active", output);
    }

    [Fact]
    public async Task AllToolsTakingGuidArguments_RejectMalformedInput_BeforeServiceCall()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);

        Assert.Contains("not a valid GUID",
            await PolicyTools.GetPolicy(service, "abc"));
        Assert.Contains("not a valid GUID",
            await PolicyTools.ListVersions(service, "abc"));
        Assert.Contains("not a valid GUID",
            await PolicyTools.GetActiveVersion(service, "abc"));
        Assert.Contains("not a valid GUID",
            await PolicyTools.GetVersion(service, "abc", Guid.NewGuid().ToString()));
        Assert.Contains("not a valid GUID",
            await PolicyTools.GetVersion(service, Guid.NewGuid().ToString(), "abc"));
    }
}
