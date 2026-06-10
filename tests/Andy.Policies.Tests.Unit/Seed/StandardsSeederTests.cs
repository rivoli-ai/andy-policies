// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Seed;

/// <summary>
/// AX standards catalog (rivoli-ai/conductor#2087): 101 development
/// standards seeded from the embedded <c>standards-seed.json</c>. These
/// tests pin the corpus invariants (count, slug prefix, enum parseability,
/// rules presence) and the seeder's idempotency contract — mirroring
/// <see cref="PolicySeederTests"/> for the guardrail six.
/// </summary>
public class StandardsSeederTests
{
    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public void EmbeddedSeed_Loads_With101UniqueStdSlugs()
    {
        var config = StandardsSeeder.LoadEmbeddedSeedConfig();

        Assert.Equal(101, config.Policies.Count);
        Assert.All(config.Policies, p => Assert.StartsWith("std-", p.Name, StringComparison.Ordinal));
        Assert.Equal(config.Policies.Count, config.Policies.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EmbeddedSeed_EveryRow_HasDescriptionScopesAndRules()
    {
        var config = StandardsSeeder.LoadEmbeddedSeedConfig();

        Assert.All(config.Policies, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Description), $"{p.Name} has no description");
            Assert.NotEmpty(p.Scopes);
            Assert.Equal(JsonValueKind.Object, p.RulesJson.ValueKind);
        });
    }

    [Fact]
    public async Task Seed_OnEmptyCatalog_Creates101ActiveV1Standards()
    {
        await using var db = NewContext();

        await StandardsSeeder.SeedStandardsAsync(db);

        Assert.Equal(101, await db.Policies.CountAsync());
        var versions = await db.PolicyVersions.ToListAsync();
        Assert.Equal(101, versions.Count);
        Assert.All(versions, v =>
        {
            Assert.Equal(1, v.Version);
            Assert.Equal(LifecycleState.Active, v.State);
            Assert.NotNull(v.PublishedAt);
            Assert.Equal(PolicySeeder.SeedSubjectId, v.PublishedBySubjectId);
            Assert.False(string.IsNullOrWhiteSpace(v.RulesJson));
        });
    }

    [Fact]
    public async Task Seed_Rerun_IsIdempotent()
    {
        await using var db = NewContext();

        await StandardsSeeder.SeedStandardsAsync(db);
        await StandardsSeeder.SeedStandardsAsync(db);

        Assert.Equal(101, await db.Policies.CountAsync());
        Assert.Equal(101, await db.PolicyVersions.CountAsync());
    }

    [Fact]
    public async Task Seed_CoexistsWithGuardrails_TotalIs107()
    {
        // The standards are a separate catalog from PolicySeeder's six
        // capability guardrails; both seed into the same table and must
        // not collide (distinct slugs).
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await StandardsSeeder.SeedStandardsAsync(db);

        Assert.Equal(107, await db.Policies.CountAsync());
    }

    [Fact]
    public async Task Seed_AgainstPopulatedCatalog_AddsOnlyMissingSlugs()
    {
        // A pre-existing standard row (e.g. operator-touched) is left
        // alone; the remaining 100 are added around it.
        await using var db = NewContext();
        var config = StandardsSeeder.LoadEmbeddedSeedConfig();
        var first = config.Policies[0];
        db.Policies.Add(new Andy.Policies.Domain.Entities.Policy
        {
            Id = Guid.NewGuid(),
            Name = first.Name,
            Description = "operator-edited description",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "operator:test",
        });
        await db.SaveChangesAsync();

        await StandardsSeeder.SeedStandardsAsync(db);

        Assert.Equal(101, await db.Policies.CountAsync());
        var preserved = await db.Policies.SingleAsync(p => p.Name == first.Name);
        Assert.Equal("operator-edited description", preserved.Description);
        Assert.Equal("operator:test", preserved.CreatedBySubjectId);
    }
}
