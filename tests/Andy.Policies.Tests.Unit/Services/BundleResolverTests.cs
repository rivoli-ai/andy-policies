// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Domain.ValueObjects;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Shared.Auditing;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="BundleResolver"/> (P8.3, story
/// rivoli-ai/andy-policies#83). Drives the resolver against EF Core
/// InMemory bundles whose <c>SnapshotJson</c> is hand-built so the
/// algorithm — exact-match filtering, dedup by PolicyVersionId, the
/// snapshot cache identity — is pinned independently of the P8.2
/// builder.
/// </summary>
public class BundleResolverTests
{
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-05-05T18:00:00Z");

    private static (BundleResolver resolver, AppDbContext db, IMemoryCache cache) NewResolver()
    {
        var db = InMemoryDbFixture.Create();
        var cache = new MemoryCache(new MemoryCacheOptions());
        return (new BundleResolver(db, cache), db, cache);
    }

    private static BundleSnapshot Snapshot(
        IEnumerable<BundlePolicyEntry>? policies = null,
        IEnumerable<BundleBindingEntry>? bindings = null,
        IEnumerable<BundleOverrideEntry>? overrides = null,
        IEnumerable<BundleScopeEntry>? scopes = null) => new(
            SchemaVersion: "1",
            CapturedAt: CapturedAt,
            AuditTailHash: new string('0', 64),
            Policies: (policies ?? Array.Empty<BundlePolicyEntry>()).ToList(),
            Bindings: (bindings ?? Array.Empty<BundleBindingEntry>()).ToList(),
            Overrides: (overrides ?? Array.Empty<BundleOverrideEntry>()).ToList(),
            Scopes: (scopes ?? Array.Empty<BundleScopeEntry>()).ToList());

    private static async Task<Bundle> SeedBundleAsync(
        AppDbContext db,
        BundleSnapshot snapshot,
        BundleState state = BundleState.Active,
        string name = "snap-1")
    {
        var canonical = CanonicalJson.SerializeObject(snapshot);
        var json = Encoding.UTF8.GetString(canonical);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(canonical))
            .ToLowerInvariant();

        var bundle = new Bundle
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAt = CapturedAt,
            CreatedBySubjectId = "test",
            SnapshotJson = json,
            SnapshotHash = hash,
            State = state,
        };
        if (state == BundleState.Deleted)
        {
            bundle.DeletedAt = CapturedAt;
            bundle.DeletedBySubjectId = "op";
        }
        db.Bundles.Add(bundle);
        await db.SaveChangesAsync();
        return bundle;
    }

    private static BundlePolicyEntry Policy(
        Guid policyVersionId, string name = "p1", int version = 1) => new(
            PolicyId: Guid.NewGuid(),
            Name: name,
            PolicyVersionId: policyVersionId,
            Version: version,
            Enforcement: "Should",
            Severity: "Moderate",
            Scopes: Array.Empty<string>(),
            RulesJson: "{}",
            Summary: "fixture");

    private static BundleBindingEntry Binding(
        Guid policyVersionId,
        string targetType = "Repo",
        string targetRef = "repo:rivoli-ai/x",
        BindStrength strength = BindStrength.Recommended) => new(
            BindingId: Guid.NewGuid(),
            PolicyVersionId: policyVersionId,
            TargetType: targetType,
            TargetRef: targetRef,
            BindStrength: strength.ToString());

    [Fact]
    public async Task Resolve_ReturnsNull_WhenBundleDoesNotExist()
    {
        var (resolver, _, _) = NewResolver();

        var result = await resolver.ResolveAsync(
            Guid.NewGuid(), BindingTargetType.Repo, "repo:any", default);

        result.Should().BeNull(
            "an unknown bundle id is a 404 path; returning an empty result " +
            "would mask consumer pinning bugs (e.g. typo'd id) as silent " +
            "no-policy answers");
    }

    [Fact]
    public async Task Resolve_ReturnsNull_WhenBundleIsSoftDeleted()
    {
        var (resolver, db, _) = NewResolver();
        var bundle = await SeedBundleAsync(db, Snapshot(), state: BundleState.Deleted);

        var result = await resolver.ResolveAsync(
            bundle.Id, BindingTargetType.Repo, "repo:any", default);

        result.Should().BeNull(
            "soft-deleted bundles must be invisible to the resolution surface; " +
            "the row remains for audit-chain integrity but is not addressable");
    }

    [Fact]
    public async Task Resolve_ReturnsEmpty_WhenNoBindingsMatchTarget()
    {
        var (resolver, db, _) = NewResolver();
        var pvId = Guid.NewGuid();
        var bundle = await SeedBundleAsync(db, Snapshot(
            policies: new[] { Policy(pvId) },
            bindings: new[] { Binding(pvId, "Repo", "repo:other") }));

        var result = await resolver.ResolveAsync(
            bundle.Id, BindingTargetType.Repo, "repo:rivoli-ai/x", default);

        result.Should().NotBeNull();
        result!.Bindings.Should().BeEmpty();
        result.SnapshotHash.Should().Be(bundle.SnapshotHash);
    }

    [Fact]
    public async Task Resolve_FiltersByExactTargetTypeAndRef()
    {
        var (resolver, db, _) = NewResolver();
        var pvA = Guid.NewGuid();
        var pvB = Guid.NewGuid();
        var bundle = await SeedBundleAsync(db, Snapshot(
            policies: new[]
            {
                Policy(pvA, "policy-a"),
                Policy(pvB, "policy-b", version: 2),
            },
            bindings: new[]
            {
                Binding(pvA, "Repo", "repo:rivoli-ai/match", BindStrength.Mandatory),
                Binding(pvB, "Repo", "repo:rivoli-ai/other"),
                Binding(pvA, "Template", "template:t1"),
            }));

        var result = await resolver.ResolveAsync(
            bundle.Id, BindingTargetType.Repo, "repo:rivoli-ai/match", default);

        result!.Bindings.Should().ContainSingle();
        result.Bindings[0].PolicyVersionId.Should().Be(pvA);
        result.Bindings[0].BindStrength.Should().Be(BindStrength.Mandatory);
    }

    [Fact]
    public async Task Resolve_DeduplicatesByPolicyVersionId_KeepingMandatory()
    {
        // Belt-and-braces: P8.2 dedups at create time via the OrderBy
        // on bindings, but a future migration that adds rows could
        // reintroduce duplicates. The resolver guards the wire
        // contract so consumers don't see redundant pairs.
        var (resolver, db, _) = NewResolver();
        var pvId = Guid.NewGuid();
        var bundle = await SeedBundleAsync(db, Snapshot(
            policies: new[] { Policy(pvId) },
            bindings: new[]
            {
                Binding(pvId, "Repo", "repo:dup", BindStrength.Recommended),
                Binding(pvId, "Repo", "repo:dup", BindStrength.Mandatory),
            }));

        var result = await resolver.ResolveAsync(
            bundle.Id, BindingTargetType.Repo, "repo:dup", default);

        result!.Bindings.Should().ContainSingle();
        result.Bindings[0].BindStrength.Should().Be(
            BindStrength.Mandatory,
            "the strictest BindStrength wins; a Recommended row beating a " +
            "Mandatory would loosen the consumer's posture without an audit " +
            "event documenting the change");
    }

    [Fact]
    public async Task Resolve_OrderingIsDeterministic_ByPolicyName()
    {
        var (resolver, db, _) = NewResolver();
        var pvA = Guid.NewGuid();
        var pvB = Guid.NewGuid();
        var pvC = Guid.NewGuid();
        var bundle = await SeedBundleAsync(db, Snapshot(
            policies: new[]
            {
                Policy(pvA, "zzz-policy"),
                Policy(pvB, "aaa-policy"),
                Policy(pvC, "mmm-policy"),
            },
            bindings: new[]
            {
                Binding(pvA, "Repo", "repo:order"),
                Binding(pvB, "Repo", "repo:order"),
                Binding(pvC, "Repo", "repo:order"),
            }));

        var result = await resolver.ResolveAsync(
            bundle.Id, BindingTargetType.Repo, "repo:order", default);

        result!.Bindings.Select(b => b.PolicyName).Should().Equal(
            "aaa-policy", "mmm-policy", "zzz-policy");
    }

    [Fact]
    public async Task GetPinnedPolicy_Returns_ForKnownPolicyId()
    {
        var (resolver, db, _) = NewResolver();
        var pvId = Guid.NewGuid();
        var policyEntry = Policy(pvId, name: "pinned-policy", version: 7);
        var bundle = await SeedBundleAsync(db, Snapshot(
            policies: new[] { policyEntry }));

        var dto = await resolver.GetPinnedPolicyAsync(bundle.Id, policyEntry.PolicyId, default);

        dto.Should().NotBeNull();
        dto!.PolicyName.Should().Be("pinned-policy");
        dto.VersionNumber.Should().Be(7);
        dto.SnapshotHash.Should().Be(bundle.SnapshotHash);
    }

    [Fact]
    public async Task GetPinnedPolicy_ReturnsNull_WhenPolicyNotInBundle()
    {
        var (resolver, db, _) = NewResolver();
        var bundle = await SeedBundleAsync(db, Snapshot());

        var dto = await resolver.GetPinnedPolicyAsync(bundle.Id, Guid.NewGuid(), default);

        dto.Should().BeNull();
    }

    [Fact]
    public async Task GetPinnedPolicy_ReturnsNull_WhenBundleSoftDeleted()
    {
        var (resolver, db, _) = NewResolver();
        var pvId = Guid.NewGuid();
        var entry = Policy(pvId);
        var bundle = await SeedBundleAsync(
            db,
            Snapshot(policies: new[] { entry }),
            state: BundleState.Deleted);

        var dto = await resolver.GetPinnedPolicyAsync(bundle.Id, entry.PolicyId, default);

        dto.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_SecondCall_HitsCache_AndProducesIdenticalResult()
    {
        // The cache identity is (bundleId, snapshotHash). Bundles are
        // immutable post-insert, so a cache hit is by definition fresh.
        // This test pins identical answers across two consecutive
        // calls — a regression that caches the wrong shape would
        // diverge on second read.
        var (resolver, db, cache) = NewResolver();
        var pvId = Guid.NewGuid();
        var bundle = await SeedBundleAsync(db, Snapshot(
            policies: new[] { Policy(pvId) },
            bindings: new[] { Binding(pvId, "Repo", "repo:cached") }));

        var first = await resolver.ResolveAsync(
            bundle.Id, BindingTargetType.Repo, "repo:cached", default);
        var second = await resolver.ResolveAsync(
            bundle.Id, BindingTargetType.Repo, "repo:cached", default);

        second.Should().BeEquivalentTo(first,
            "second-call answer must match the first byte-for-byte; a cache " +
            "hit that produced a different shape would mean the resolver is " +
            "mutating the cached snapshot in place");
    }

    [Fact]
    public async Task EffectiveResolution_MatchesEveryBridgeTargetAcrossAncestorChain()
    {
        var (resolver, db, _) = NewResolver();
        var org = new BundleScopeEntry(Guid.NewGuid(), null, "Org", "org:acme", "Acme");
        var tenant = new BundleScopeEntry(Guid.NewGuid(), org.ScopeNodeId, "Tenant", "tenant:t1", "T1");
        var team = new BundleScopeEntry(Guid.NewGuid(), tenant.ScopeNodeId, "Team", "team:red", "Red");
        var repo = new BundleScopeEntry(Guid.NewGuid(), team.ScopeNodeId, "Repo", "repo:acme/svc", "Svc");
        var template = new BundleScopeEntry(Guid.NewGuid(), repo.ScopeNodeId, "Template", "template:deploy", "Deploy");
        var versions = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var policies = versions.Select((id, i) => Policy(id, $"p{i}")).ToArray();
        var bindings = new[]
        {
            Binding(versions[0], "Org", org.Ref),
            Binding(versions[1], "Tenant", tenant.Ref),
            Binding(versions[2], "ScopeNode", $"scope:{team.ScopeNodeId}"),
            Binding(versions[3], "Repo", repo.Ref),
            Binding(versions[4], "Template", template.Ref),
        };
        var bundle = await SeedBundleAsync(db, Snapshot(
            policies, bindings, scopes: new[] { org, tenant, team, repo, template }));

        var result = await resolver.ResolveEffectiveForScopeAsync(bundle.Id, template.ScopeNodeId);

        result!.Policies.Should().HaveCount(5);
        result.Policies.Select(p => p.SourceBindingId).Should()
            .BeEquivalentTo(bindings.Select(b => b.BindingId));
        result.Policies.Single(p => p.PolicyVersionId == versions[0]).SourceScopeNodeId.Should().Be(org.ScopeNodeId);
        result.Policies.Single(p => p.PolicyVersionId == versions[4]).SourceScopeNodeId.Should().Be(template.ScopeNodeId);
    }

    [Fact]
    public async Task EffectiveResolution_AppliesPinnedPrincipalAndCohortOverridesWithExplanation()
    {
        var (resolver, db, _) = NewResolver();
        var scope = new BundleScopeEntry(Guid.NewGuid(), null, "Repo", "repo:acme/svc", "Svc");
        var baselineId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        var baseline = Policy(baselineId, "baseline");
        var replacement = Policy(replacementId, "replacement", 2);
        var binding = Binding(baselineId, "Repo", scope.Ref, BindStrength.Mandatory);
        var cohort = new BundleOverrideEntry(
            Guid.NewGuid(), baselineId, "Cohort", "cohort:beta", "Replace",
            replacementId, CapturedAt.AddDays(1), replacement, CapturedAt.AddMinutes(-2));
        var principal = new BundleOverrideEntry(
            Guid.NewGuid(), baselineId, "Principal", "user:42", "Exempt",
            null, CapturedAt.AddDays(1), null, CapturedAt.AddMinutes(-1));
        var bundle = await SeedBundleAsync(db, Snapshot(
            new[] { baseline }, new[] { binding }, new[] { cohort, principal }, new[] { scope }));

        var principalResult = await resolver.ResolveEffectiveForScopeAsync(
            bundle.Id, scope.ScopeNodeId,
            new OverrideResolutionContext("user:42", new[] { "cohort:beta" }));
        var cohortResult = await resolver.ResolveEffectiveForScopeAsync(
            bundle.Id, scope.ScopeNodeId,
            new OverrideResolutionContext("user:7", new[] { "cohort:beta" }));

        principalResult!.Policies.Should().BeEmpty("principal overrides take precedence over cohort matches");
        principalResult.AppliedOverrides.Should().ContainSingle().Which.OverrideId.Should().Be(principal.OverrideId);
        cohortResult!.Policies.Should().ContainSingle().Which.PolicyVersionId.Should().Be(replacementId);
        cohortResult.AppliedOverrides.Should().ContainSingle().Which.OverrideId.Should().Be(cohort.OverrideId);
    }

    [Fact]
    public async Task EffectiveResolution_OldSnapshotWithoutOverrides_PreservesBaseline()
    {
        var (resolver, db, _) = NewResolver();
        var scope = new BundleScopeEntry(Guid.NewGuid(), null, "Repo", "repo:legacy", "Legacy");
        var versionId = Guid.NewGuid();
        var bundle = await SeedBundleAsync(db, Snapshot(
            new[] { Policy(versionId) }, new[] { Binding(versionId, "Repo", scope.Ref) },
            scopes: new[] { scope }));

        var result = await resolver.ResolveEffectiveForScopeAsync(
            bundle.Id, scope.ScopeNodeId,
            new OverrideResolutionContext("user:42", new[] { "cohort:beta" }));

        result!.Policies.Should().ContainSingle().Which.PolicyVersionId.Should().Be(versionId);
        result.AppliedOverrides.Should().BeEmpty();
    }
}
