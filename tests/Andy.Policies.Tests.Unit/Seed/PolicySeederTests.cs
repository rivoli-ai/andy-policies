// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Seed;

/// <summary>
/// P1.3 (#73): the six stock policies are a product requirement that downstream
/// services (Conductor admission, andy-tasks gates) reference by stable slug from
/// day one. These tests pin the contents of the seed table row-by-row so an
/// accidental edit (slug rename, scope drop, severity change) breaks loudly here.
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
    public async Task Seed_CreatesSixDraftVersionsAtVersionOne()
    {
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);

        var versions = await db.PolicyVersions.ToListAsync();
        Assert.Equal(6, versions.Count);
        Assert.All(versions, v =>
        {
            Assert.Equal(1, v.Version);
            Assert.Equal(LifecycleState.Draft, v.State);
        });
    }

    [Fact]
    public async Task Seed_IsIdempotentWhenCatalogNonEmpty()
    {
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await PolicySeeder.SeedStockPoliciesAsync(db);

        Assert.Equal(6, await db.Policies.CountAsync());
        Assert.Equal(6, await db.PolicyVersions.CountAsync());
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
        });
    }

    public static IEnumerable<object[]> StockMappingRows() => new[]
    {
        new object[] { "read-only",    EnforcementLevel.Must,   Severity.Info,     Array.Empty<string>() },
        new object[] { "write-branch", EnforcementLevel.Should, Severity.Moderate, new[] { "repo" } },
        new object[] { "sandboxed",    EnforcementLevel.Must,   Severity.Moderate, new[] { "tool", "container" } },
        new object[] { "draft-only",   EnforcementLevel.Must,   Severity.Info,     new[] { "template" } },
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
        var slugs = PolicySeeder.StockPolicies.Select(s => s.Name).ToList();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }
}
