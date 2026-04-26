// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Tests.Integration.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Bootstrap;

/// <summary>
/// P1.3 (#73): the seed wiring in <c>Program.cs</c> must run during host
/// startup so a fresh boot leaves the catalog with the six canonical stock
/// policies. The unit-level tests cover the seeder in isolation; this fixture
/// proves the boot pipeline (auto-migrate -> seed) is connected correctly.
/// </summary>
public class SeedOnStartupTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;

    public SeedOnStartupTests(PoliciesApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OnFirstBoot_CatalogContainsAllSixStockPolicies()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var slugs = await db.Policies.Select(p => p.Name).OrderBy(s => s).ToListAsync();

        Assert.Equal(
            new[] { "draft-only", "high-risk", "no-prod", "read-only", "sandboxed", "write-branch" },
            slugs);
    }

    [Fact]
    public async Task OnFirstBoot_EveryStockPolicyHasOneDraftVersionAtVersionOne()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var versions = await db.PolicyVersions.ToListAsync();

        Assert.Equal(6, versions.Count);
        Assert.All(versions, v =>
        {
            Assert.Equal(1, v.Version);
            Assert.Equal(LifecycleState.Draft, v.State);
            Assert.Equal(PolicySeeder.SeedSubjectId, v.CreatedBySubjectId);
            Assert.Equal(PolicySeeder.SeedSubjectId, v.ProposerSubjectId);
        });
    }

    [Fact]
    public async Task SecondSeedCall_DoesNotDuplicateOrMutate()
    {
        // Re-running the seeder against the live factory's already-seeded DB
        // must be a no-op. This is the "every boot is safe" invariant from
        // the story: operator-edited rows survive restarts.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var beforeIds = await db.Policies.OrderBy(p => p.Name).Select(p => p.Id).ToListAsync();

        await PolicySeeder.SeedStockPoliciesAsync(db);

        var afterIds = await db.Policies.OrderBy(p => p.Name).Select(p => p.Id).ToListAsync();
        Assert.Equal(beforeIds, afterIds);
        Assert.Equal(6, await db.Policies.CountAsync());
        Assert.Equal(6, await db.PolicyVersions.CountAsync());
    }

    [Fact]
    public async Task SeededPolicies_AreReachableViaTheRestSurface()
    {
        // End-to-end check: the slugs the seeder writes must resolve through
        // the public `/api/policies/by-name/{slug}` route that consumers use.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/policies/by-name/no-prod");
        response.EnsureSuccessStatusCode();

        var policy = await response.Content.ReadFromJsonAsync<PolicyDto>();
        Assert.NotNull(policy);
        Assert.Equal("no-prod", policy!.Name);
        Assert.Equal(1, policy.VersionCount);
    }
}
