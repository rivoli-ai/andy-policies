// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Seed;

/// <summary>
/// SD4.2 (rivoli-ai/andy-policies#1182): the default agent → policy
/// bindings are a cross-service product contract — Conductor's ActionBus,
/// andy-tasks gates, and andy-agents observability all join on the
/// (agent slug, policy slug) edge. These tests pin the edge set row-by-row
/// so a drive-by edit to <c>BindingSeeder.SeedBindings</c> or
/// <c>config/bindings-seed.json</c> breaks loudly.
/// </summary>
public class BindingSeederTests
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
    public async Task Seed_OnFreshDb_CreatesNineteenBindings()
    {
        // SD4.2 fixture: 19 unique (agent, policy) edges, see SeedBindings
        // table. Six agents × universal guardrails (no-prod, high-risk) = 12,
        // plus role-specific bindings:
        //   triage / research / review -> read-only             (3)
        //   planning -> draft-only                              (1)
        //   coding -> write-branch + sandboxed                  (2)
        //   validation -> sandboxed                             (1)
        // Total = 12 + 3 + 1 + 2 + 1 = 19.
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        var bindings = await db.Bindings.AsNoTracking()
            .Where(b => b.TargetType == BindingTargetType.Agent)
            .ToListAsync();
        Assert.Equal(19, bindings.Count);
    }

    [Fact]
    public async Task Seed_IsIdempotent_ReseedDoesNotDuplicate()
    {
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        Assert.Equal(19, await db.Bindings.CountAsync(b => b.TargetType == BindingTargetType.Agent));
    }

    [Fact]
    public async Task Seed_DoesNotBumpBundleSnapshot_AcrossReseeds()
    {
        // SD4 idempotency contract: re-running the seeder must not touch
        // the bundles table. Bundles are created on demand via
        // BundleService.CreateAsync, never auto-snapshot on policy/binding
        // writes — the seeder rerun is invisible to bundle consumers.
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);
        var bundleCountAfterFirstSeed = await db.Bundles.CountAsync();

        await BindingSeeder.SeedDefaultBindingsAsync(db);
        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        Assert.Equal(0, bundleCountAfterFirstSeed);
        Assert.Equal(0, await db.Bundles.CountAsync());
    }

    [Fact]
    public async Task Seed_AllBindingsTargetAgentTypeWithCanonicalRefShape()
    {
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        var bindings = await db.Bindings.AsNoTracking().ToListAsync();
        Assert.All(bindings, b =>
        {
            Assert.Equal(BindingTargetType.Agent, b.TargetType);
            Assert.StartsWith(BindingSeeder.AgentTargetRefPrefix, b.TargetRef);
            var slug = b.TargetRef[BindingSeeder.AgentTargetRefPrefix.Length..];
            Assert.Contains(slug, BindingSeeder.SeedAgentSlugs);
        });
    }

    [Fact]
    public async Task Seed_BindsToActivePolicyVersionOnly()
    {
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        var bindings = await db.Bindings.AsNoTracking().ToListAsync();
        var versionStates = await db.PolicyVersions
            .AsNoTracking()
            .ToDictionaryAsync(v => v.Id, v => v.State);

        Assert.All(bindings, b =>
        {
            Assert.True(versionStates.TryGetValue(b.PolicyVersionId, out var state),
                $"Binding {b.Id} points at unknown PolicyVersion {b.PolicyVersionId}");
            Assert.Equal(LifecycleState.Active, state);
        });
    }

    [Fact]
    public async Task Seed_AllBindingsAreMandatory()
    {
        // SD4.2 acceptance: the canonical seed pins all six agents with
        // Mandatory strength — consumers block on violation rather than
        // warn. A future story may relax some pairs, but the seed shipped
        // by this PR is uniformly Mandatory.
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        var bindings = await db.Bindings.AsNoTracking().ToListAsync();
        Assert.All(bindings, b => Assert.Equal(BindStrength.Mandatory, b.BindStrength));
    }

    [Fact]
    public async Task Seed_SkipsBindingsWhosePolicyIsNotYetSeeded()
    {
        // Crash-free partial-seed contract: if PolicySeeder has not run
        // (or a slug is missing for any reason), BindingSeeder skips
        // those rows silently and inserts what it can. Next boot picks
        // up the gap. We exercise the path with the policy seeder
        // skipped entirely.
        await using var db = NewContext();

        await BindingSeeder.SeedDefaultBindingsAsync(db);

        Assert.Equal(0, await db.Bindings.CountAsync());
    }

    public static IEnumerable<object[]> ExpectedEdges()
    {
        // Pinned mapping table. Each row asserts a single (agent, policy)
        // edge exists exactly once in the seed.
        var rows = new (string agent, string policy)[]
        {
            // Read-only triad.
            ("triage", "read-only"),
            ("research", "read-only"),
            ("review", "read-only"),
            // Planning.
            ("planning", "draft-only"),
            // Coding.
            ("coding", "write-branch"),
            ("coding", "sandboxed"),
            // Validation.
            ("validation", "sandboxed"),
            ("validation", "no-prod"),
            // Universal guardrails — no-prod across all six agents
            // (validation already covered above).
            ("triage", "no-prod"),
            ("research", "no-prod"),
            ("planning", "no-prod"),
            ("coding", "no-prod"),
            ("review", "no-prod"),
            // Universal guardrails — high-risk across all six agents.
            ("triage", "high-risk"),
            ("research", "high-risk"),
            ("planning", "high-risk"),
            ("coding", "high-risk"),
            ("validation", "high-risk"),
            ("review", "high-risk"),
        };
        foreach (var (agent, policy) in rows)
        {
            yield return new object[] { agent, policy };
        }
    }

    [Theory]
    [MemberData(nameof(ExpectedEdges))]
    public async Task Seed_CreatesExpectedAgentPolicyEdge(string agent, string policy)
    {
        await using var db = NewContext();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        var policyRow = await db.Policies.SingleAsync(p => p.Name == policy);
        var version = await db.PolicyVersions
            .SingleAsync(v => v.PolicyId == policyRow.Id && v.State == LifecycleState.Active);
        var binding = await db.Bindings.AsNoTracking().SingleAsync(b =>
            b.PolicyVersionId == version.Id
            && b.TargetType == BindingTargetType.Agent
            && b.TargetRef == BindingSeeder.AgentTargetRefPrefix + agent);

        Assert.Equal(BindStrength.Mandatory, binding.BindStrength);
        Assert.Equal(PolicySeeder.SeedSubjectId, binding.CreatedBySubjectId);
        Assert.Null(binding.DeletedAt);
    }

    [Fact]
    public void SeedBindings_AreDeduped()
    {
        // The seed table itself must not contain duplicate (agent, policy)
        // pairs — even though the seeder dedupes against the DB, an
        // accidentally-duplicated row would surface as a misleading "size 20".
        var pairs = BindingSeeder.SeedBindings
            .Select(b => (b.AgentSlug, b.PolicySlug))
            .ToList();
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
    }

    [Fact]
    public void SeedAgentSlugs_AreExactlyTheSixSD2Agents()
    {
        Assert.Equal(
            new[] { "coding", "planning", "research", "review", "triage", "validation" },
            BindingSeeder.SeedAgentSlugs.OrderBy(s => s).ToArray());
    }

    [Fact]
    public void SeedBindings_OnlyReferenceKnownAgentSlugs()
    {
        // SD4.2 acceptance: every binding's TargetRef must reference one of
        // the six known agent slugs. We validate against the embedded
        // fixture so the test does not depend on a live andy-agents
        // service.
        var known = new HashSet<string>(BindingSeeder.SeedAgentSlugs, StringComparer.Ordinal);
        Assert.All(BindingSeeder.SeedBindings, b =>
            Assert.Contains(b.AgentSlug, known));
    }

    [Fact]
    public void SeedBindings_OnlyReferenceCanonicalPolicySlugs()
    {
        var known = PolicySeeder.StockPolicies.Select(s => s.Slug).ToHashSet(StringComparer.Ordinal);
        Assert.All(BindingSeeder.SeedBindings, b =>
            Assert.Contains(b.PolicySlug, known));
    }

    [Fact]
    public void SeedConfigJson_OnDisk_MatchesEmbeddedBindingsTable()
    {
        // SD4.2 parity contract: the embedded SeedBindings/SeedAgentSlugs
        // tables and config/bindings-seed.json must agree row-for-row.
        var path = FindRepoFile(BindingSeeder.SeedConfigRelativePath);
        var config = BindingSeeder.LoadSeedConfig(path);

        Assert.Equal(BindingSeeder.SeedAgentSlugs.ToList(), config.Agents);

        Assert.Equal(BindingSeeder.SeedBindings.Count, config.Bindings.Count);
        foreach (var (embedded, json) in BindingSeeder.SeedBindings.Zip(config.Bindings))
        {
            Assert.Equal(embedded.AgentSlug, json.Agent);
            Assert.Equal(embedded.PolicySlug, json.Policy);
            Assert.Equal(embedded.BindStrength.ToString(), json.BindStrength);
        }
    }

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
