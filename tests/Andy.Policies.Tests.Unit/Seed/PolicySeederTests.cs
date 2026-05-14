// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Seed;

/// <summary>
/// P1.3 (#73) + SD4.1 (#1181): the six canonical lifecycle policies are a
/// product requirement that downstream services (Conductor admission,
/// andy-tasks gates, SD2 agents, SD5 task templates) reference by stable
/// slug from day one. These tests pin the contents of the seed table row-
/// by-row so an accidental edit (slug rename, scope drop, severity change,
/// state flip) breaks loudly here.
/// </summary>
public class PolicySeederTests
{
    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // The InMemory provider warns on raw-SQL/transaction features that the
            // real provider supports — irrelevant here, silencing keeps the test
            // signal clean.
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Seed_OnEmptyCatalog_CreatesSixPolicies()
    {
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);

        Assert.Equal(6, await db.Policies.CountAsync());
    }

    [Fact]
    public async Task Seed_CreatesSixActiveVersionsAtVersionOne()
    {
        // SD4.1 #1181 contract: seed lands in Active state directly, so
        // downstream binders can attach without driving the Draft -> Active
        // lifecycle dance.
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);

        var versions = await db.PolicyVersions.ToListAsync();
        Assert.Equal(6, versions.Count);
        Assert.All(versions, v =>
        {
            Assert.Equal(1, v.Version);
            Assert.Equal(LifecycleState.Active, v.State);
            // PublishedAt + PublishedBySubjectId stamped to match what
            // LifecycleTransitionService.Publish would have written.
            Assert.NotNull(v.PublishedAt);
            Assert.Equal(PolicySeeder.SeedSubjectId, v.PublishedBySubjectId);
        });
    }

    [Fact]
    public async Task Seed_IsIdempotentWhenCatalogPopulated()
    {
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await PolicySeeder.SeedStockPoliciesAsync(db);

        Assert.Equal(6, await db.Policies.CountAsync());
        Assert.Equal(6, await db.PolicyVersions.CountAsync());
    }

    [Fact]
    public async Task Seed_IsPerRowIdempotent_OnPartialCatalog()
    {
        // SD4 idempotency contract: a partially-populated catalog (e.g.
        // operator added their own policy, or the seed file gained a new
        // canonical slug) tops up only the missing canonical rows, never
        // touching the rest.
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);

        var preExisting = await db.Policies
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => p.Id)
            .ToListAsync();

        // Operator drops one canonical slug, leaving five.
        var sandbox = await db.Policies.FirstAsync(p => p.Name == "sandboxed");
        var sandboxVersion = await db.PolicyVersions.FirstAsync(v => v.PolicyId == sandbox.Id);
        db.PolicyVersions.Remove(sandboxVersion);
        db.Policies.Remove(sandbox);
        await db.SaveChangesAsync();

        Assert.Equal(5, await db.Policies.CountAsync());

        // Reseed must re-add 'sandboxed' and leave the other five untouched.
        await PolicySeeder.SeedStockPoliciesAsync(db);

        Assert.Equal(6, await db.Policies.CountAsync());
        var preExistingSurvivors = preExisting
            .Where(id => id != sandbox.Id)
            .ToHashSet();
        var afterIds = await db.Policies
            .AsNoTracking()
            .Where(p => p.Name != "sandboxed")
            .Select(p => p.Id)
            .ToListAsync();
        Assert.True(afterIds.All(preExistingSurvivors.Contains),
            "Pre-existing policy rows must keep their ids across reseed.");
    }

    [Fact]
    public async Task Seed_AssignsSeedSubjectIdOnPolicyAndVersion()
    {
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);

        Assert.All(await db.Policies.ToListAsync(),
            p => Assert.Equal(PolicySeeder.SeedSubjectId, p.CreatedBySubjectId));
        Assert.All(await db.PolicyVersions.ToListAsync(), v =>
        {
            Assert.Equal(PolicySeeder.SeedSubjectId, v.CreatedBySubjectId);
            Assert.Equal(PolicySeeder.SeedSubjectId, v.ProposerSubjectId);
            Assert.Equal(PolicySeeder.SeedSubjectId, v.PublishedBySubjectId);
        });
    }

    [Fact]
    public async Task Seed_RulesJsonParsesAsJson_ForEveryStockPolicy()
    {
        // Service-side rules validation (PolicyService.ValidateRulesJson)
        // checks (1) parses as JSON and (2) <= 64 KiB. The seeded rows
        // must satisfy both so the catalog is queryable end-to-end on
        // first boot.
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);

        var versions = await db.PolicyVersions.ToListAsync();
        Assert.All(versions, v =>
        {
            Assert.True(v.RulesJson.Length <= 65536,
                $"RulesJson for version {v.Id} exceeds 64 KiB.");
            // Round-trip parse — the seed file must produce valid JSON.
            using var doc = JsonDocument.Parse(v.RulesJson);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        });
    }

    [Fact]
    public async Task Seed_HighRiskPolicy_DeclaresApproverChain()
    {
        // SD4.1 acceptance: 'high-risk' requires a typed-confirmation
        // approver chain. The opaque rulesJson DSL is where that contract
        // lives (the catalog is rules-DSL-agnostic per the SchemasController
        // permissive schema), so we assert the embedded shape.
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);

        var highRisk = await db.Policies.SingleAsync(p => p.Name == "high-risk");
        var version = await db.PolicyVersions.SingleAsync(v => v.PolicyId == highRisk.Id);
        using var doc = JsonDocument.Parse(version.RulesJson);
        var approvers = doc.RootElement.GetProperty("approvers");
        Assert.Equal(JsonValueKind.Array, approvers.ValueKind);
        Assert.Equal(1, approvers.GetArrayLength());
        Assert.Equal("maintainer", approvers[0].GetProperty("role").GetString());
        Assert.True(doc.RootElement.GetProperty("requireTypedConfirmation").GetBoolean());
    }

    public static IEnumerable<object[]> StockMappingRows() => new[]
    {
        new object[] { "read-only",    EnforcementLevel.Must,   Severity.Info,     Array.Empty<string>() },
        new object[] { "draft-only",   EnforcementLevel.Must,   Severity.Info,     new[] { "template" } },
        new object[] { "write-branch", EnforcementLevel.Should, Severity.Moderate, new[] { "repo" } },
        new object[] { "sandboxed",    EnforcementLevel.Must,   Severity.Moderate, new[] { "tool", "container" } },
        new object[] { "no-prod",      EnforcementLevel.Must,   Severity.Critical, new[] { "prod" } },
        new object[] { "high-risk",    EnforcementLevel.Must,   Severity.Critical, Array.Empty<string>() },
    };

    [Theory]
    [MemberData(nameof(StockMappingRows))]
    public async Task Seed_AssignsCorrectDimensionsPerStockPolicy(
        string slug,
        EnforcementLevel expectedEnforcement,
        Severity expectedSeverity,
        string[] expectedScopes)
    {
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);

        var policy = await db.Policies.SingleAsync(p => p.Name == slug);
        var version = await db.PolicyVersions.SingleAsync(v => v.PolicyId == policy.Id);

        Assert.Equal(expectedEnforcement, version.Enforcement);
        Assert.Equal(expectedSeverity, version.Severity);
        Assert.Equal(expectedScopes, version.Scopes);
    }

    [Fact]
    public void StockPolicies_ListIsExactlySix()
    {
        // Guards against accidental additions/removals: a new stock policy is a
        // product decision that should land in this issue trail, not by drive-by
        // edit. Bump the expected count when intentionally extending the catalog.
        Assert.Equal(6, PolicySeeder.StockPolicies.Count);
    }

    [Fact]
    public void StockPolicies_SlugsAreUnique()
    {
        var slugs = PolicySeeder.StockPolicies.Select(s => s.Slug).ToList();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }

    [Fact]
    public void SeedConfigJson_OnDisk_MatchesEmbeddedStockTable()
    {
        // SD4.1 parity contract: the embedded StockPolicies table and the
        // config/policies-seed.json file must agree on slug, severity,
        // enforcement, and scopes. Either side drifts -> this test fails.
        var path = FindRepoFile(PolicySeeder.SeedConfigRelativePath);
        var config = PolicySeeder.LoadSeedConfig(path);

        Assert.Equal(PolicySeeder.StockPolicies.Count, config.Policies.Count);
        foreach (var (embedded, json) in PolicySeeder.StockPolicies.Zip(config.Policies))
        {
            Assert.Equal(embedded.Slug, json.Id);
            Assert.Equal(embedded.Slug, json.Name);
            Assert.Equal(embedded.Description, json.Description);
            Assert.Equal(embedded.Severity.ToString().ToLowerInvariant(), json.Severity);
            Assert.Equal(EnforcementToWire(embedded.Enforcement), json.Enforcement);
            Assert.Equal(embedded.Scopes.ToList(), json.Scopes);
        }
    }

    private static string EnforcementToWire(EnforcementLevel e) => e switch
    {
        EnforcementLevel.May => "MAY",
        EnforcementLevel.Should => "SHOULD",
        EnforcementLevel.Must => "MUST",
        _ => throw new InvalidOperationException($"Unmapped enforcement level {e}."),
    };

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Andy.Policies.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, relativePath);
        Assert.True(File.Exists(path), $"{relativePath} should exist at the repo root.");
        return path;
    }
}
