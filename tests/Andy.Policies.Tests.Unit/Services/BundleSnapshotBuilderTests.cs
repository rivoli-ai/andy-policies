// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Shared.Auditing;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="BundleSnapshotBuilder"/> (P8.2, story
/// rivoli-ai/andy-policies#82). Drives the builder over EF Core
/// InMemory to pin: filtering (Active-only policies, non-deleted
/// bindings against Active versions, Approved+unexpired overrides),
/// stable ordering, audit-tail-hash extraction, and hash determinism.
/// </summary>
public class BundleSnapshotBuilderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-05-05T18:00:00Z");

    private static AppDbContext NewDb() => InMemoryDbFixture.Create();

    private static async Task<(Guid policyId, Guid versionId)> SeedPolicyVersionAsync(
        AppDbContext db, string name = "p1", LifecycleState state = LifecycleState.Active)
    {
        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedBySubjectId = "u1",
        };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            Version = 1,
            State = state,
            Enforcement = EnforcementLevel.Should,
            Severity = Severity.Moderate,
            Scopes = new List<string>(),
            Summary = "fixture",
            RulesJson = "{}",
            CreatedAt = Now.AddDays(-1),
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return (policy.Id, version.Id);
    }

    [Fact]
    public async Task Build_FiltersToActivePolicyVersionsOnly()
    {
        await using var db = NewDb();
        var (_, activeId) = await SeedPolicyVersionAsync(db, "active-policy", LifecycleState.Active);
        await SeedPolicyVersionAsync(db, "draft-policy", LifecycleState.Draft);
        await SeedPolicyVersionAsync(db, "winddown-policy", LifecycleState.WindingDown);
        await SeedPolicyVersionAsync(db, "retired-policy", LifecycleState.Retired);

        var snapshot = await new BundleSnapshotBuilder(db).BuildAsync(Now);

        snapshot.Policies.Should().ContainSingle()
            .Which.PolicyVersionId.Should().Be(
                activeId,
                "Draft / WindingDown / Retired versions have no runtime " +
                "effect for consumers; including them would mislead a " +
                "pinned bundle into representing inactive history");
    }

    [Fact]
    public async Task Build_FiltersOutDeletedBindings()
    {
        await using var db = NewDb();
        var (_, versionId) = await SeedPolicyVersionAsync(db);
        db.Bindings.AddRange(
            new Binding
            {
                Id = Guid.NewGuid(),
                PolicyVersionId = versionId,
                TargetType = BindingTargetType.Repo,
                TargetRef = "repo:rivoli-ai/live",
                BindStrength = BindStrength.Mandatory,
                CreatedBySubjectId = "u1",
            },
            new Binding
            {
                Id = Guid.NewGuid(),
                PolicyVersionId = versionId,
                TargetType = BindingTargetType.Repo,
                TargetRef = "repo:rivoli-ai/dead",
                BindStrength = BindStrength.Recommended,
                CreatedBySubjectId = "u1",
                DeletedAt = Now.AddHours(-1),
                DeletedBySubjectId = "op",
            });
        await db.SaveChangesAsync();

        var snapshot = await new BundleSnapshotBuilder(db).BuildAsync(Now);

        snapshot.Bindings.Should().ContainSingle()
            .Which.TargetRef.Should().Be("repo:rivoli-ai/live");
    }

    [Fact]
    public async Task Build_FiltersOutBindingsAgainstNonActiveVersions()
    {
        await using var db = NewDb();
        var (_, _) = await SeedPolicyVersionAsync(db, "draft-only", LifecycleState.Draft);
        var (_, draftId) = await SeedPolicyVersionAsync(db, "another-draft", LifecycleState.Draft);
        db.Bindings.Add(new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = draftId,
            TargetType = BindingTargetType.Repo,
            TargetRef = "repo:to/draft",
            BindStrength = BindStrength.Recommended,
            CreatedBySubjectId = "u1",
        });
        await db.SaveChangesAsync();

        var snapshot = await new BundleSnapshotBuilder(db).BuildAsync(Now);

        snapshot.Bindings.Should().BeEmpty(
            "a bundle's bindings reference policy versions that the bundle " +
            "actually carries; a binding to an inactive version has no " +
            "addressable policy in the snapshot");
    }

    [Fact]
    public async Task Build_FiltersOutNonApprovedAndExpiredOverrides()
    {
        await using var db = NewDb();
        var (_, versionId) = await SeedPolicyVersionAsync(db);
        db.Overrides.AddRange(
            new Override
            {
                Id = Guid.NewGuid(),
                PolicyVersionId = versionId,
                ScopeKind = OverrideScopeKind.Principal,
                ScopeRef = "user:approved-live",
                Effect = OverrideEffect.Exempt,
                State = OverrideState.Approved,
                ExpiresAt = Now.AddDays(7),
                ProposerSubjectId = "alice",
                Rationale = "live",
                ProposedAt = Now.AddDays(-1),
            },
            new Override
            {
                Id = Guid.NewGuid(),
                PolicyVersionId = versionId,
                ScopeKind = OverrideScopeKind.Principal,
                ScopeRef = "user:approved-expired",
                Effect = OverrideEffect.Exempt,
                State = OverrideState.Approved,
                ExpiresAt = Now.AddSeconds(-1),
                ProposerSubjectId = "alice",
                Rationale = "expired",
                ProposedAt = Now.AddDays(-2),
            },
            new Override
            {
                Id = Guid.NewGuid(),
                PolicyVersionId = versionId,
                ScopeKind = OverrideScopeKind.Principal,
                ScopeRef = "user:proposed",
                Effect = OverrideEffect.Exempt,
                State = OverrideState.Proposed,
                ExpiresAt = Now.AddDays(7),
                ProposerSubjectId = "alice",
                Rationale = "pending",
                ProposedAt = Now.AddHours(-1),
            });
        await db.SaveChangesAsync();

        var snapshot = await new BundleSnapshotBuilder(db).BuildAsync(Now);

        snapshot.Overrides.Should().ContainSingle()
            .Which.ScopeRef.Should().Be(
                "user:approved-live",
                "only Approved overrides whose ExpiresAt is strictly after " +
                "capturedAt have runtime effect; everything else is past " +
                "or pending");
    }

    [Fact]
    public async Task Build_WithIncludeOverridesFalse_EmitsEmptyOverridesList()
    {
        // P9 follow-up #205 (2026-05-07): when CreateBundleRequest sets
        // IncludeOverrides=false, the builder must skip the override
        // scan even if Approved+non-expired rows exist. Used by
        // compliance/immutability bundles whose runtime behaviour is
        // governed strictly by the active policy + binding set.
        await using var db = NewDb();
        var (_, versionId) = await SeedPolicyVersionAsync(db);
        db.Overrides.Add(new Override
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = versionId,
            ScopeKind = OverrideScopeKind.Principal,
            ScopeRef = "user:would-be-included",
            Effect = OverrideEffect.Exempt,
            State = OverrideState.Approved,
            ExpiresAt = Now.AddDays(7),
            ProposerSubjectId = "alice",
            Rationale = "live",
            ProposedAt = Now.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var snapshot = await new BundleSnapshotBuilder(db)
            .BuildAsync(Now, includeOverrides: false);

        snapshot.Overrides.Should().BeEmpty(
            "IncludeOverrides=false elides the override scan entirely");
        // Sanity check: policies + scopes still load — the flag only
        // affects the override slice.
        snapshot.Policies.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Build_OrdersCollectionsStably()
    {
        await using var db = NewDb();
        // Seed two policies with names that would sort differently if
        // alphabetised; the spec requires PolicyId+Version ordering, so
        // the snapshot order is determined by GUID, not name.
        var (p1, v1) = await SeedPolicyVersionAsync(db, "zzz-policy");
        var (p2, v2) = await SeedPolicyVersionAsync(db, "aaa-policy");

        var snapshot = await new BundleSnapshotBuilder(db).BuildAsync(Now);

        var order = snapshot.Policies.Select(p => p.PolicyId).ToList();
        order.Should().Equal(
            new[] { p1, p2 }.OrderBy(g => g).ToList(),
            "the Policies array must be ordered by PolicyId so canonical " +
            "JSON serialisation produces byte-identical output across runs");
    }

    [Fact]
    public async Task Build_PopulatesAuditTailHashFromMostRecentEvent()
    {
        await using var db = NewDb();
        await SeedPolicyVersionAsync(db);
        var newestHash = new byte[32];
        new Random(42).NextBytes(newestHash);
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            Seq = 1,
            PrevHash = new byte[32],
            Hash = new byte[32], // older
            Timestamp = Now.AddDays(-1),
            ActorSubjectId = "u1",
            ActorRoles = Array.Empty<string>(),
            Action = "policy.create",
            EntityType = "Policy",
            EntityId = "x",
            FieldDiffJson = "[]",
        });
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            Seq = 2,
            PrevHash = new byte[32],
            Hash = newestHash,
            Timestamp = Now,
            ActorSubjectId = "u1",
            ActorRoles = Array.Empty<string>(),
            Action = "policy.publish",
            EntityType = "Policy",
            EntityId = "x",
            FieldDiffJson = "[]",
        });
        await db.SaveChangesAsync();

        var snapshot = await new BundleSnapshotBuilder(db).BuildAsync(Now);

        snapshot.AuditTailHash.Should().Be(
            Convert.ToHexString(newestHash).ToLowerInvariant(),
            "the chain's tail is the highest-Seq row's Hash; this is the " +
            "coordinate consumers cross-walk with the bundle.create event");
    }

    [Fact]
    public async Task Build_AuditTailHash_IsZeroHexWhenChainIsEmpty()
    {
        await using var db = NewDb();
        await SeedPolicyVersionAsync(db);

        var snapshot = await new BundleSnapshotBuilder(db).BuildAsync(Now);

        snapshot.AuditTailHash.Should().Be(
            new string('0', 64),
            "an empty chain matches the genesis prev-hash convention from " +
            "the audit chain so verifiers can treat the empty case identically");
    }

    [Fact]
    public async Task Build_IsDeterministic_AcrossTwoIndependentContexts()
    {
        // Same seeded catalog → same canonical JSON → same SHA-256.
        // This is the floor under the SnapshotHash invariant.
        var dbName = Guid.NewGuid().ToString();
        byte[] firstHash, secondHash;

        await using (var db1 = InMemoryDbFixture.Create(dbName))
        {
            await SeedPolicyVersionAsync(db1, "det");
            firstHash = SHA256.HashData(
                CanonicalJson.SerializeObject(
                    await new BundleSnapshotBuilder(db1).BuildAsync(Now)));
        }

        await using (var db2 = InMemoryDbFixture.Create(dbName))
        {
            secondHash = SHA256.HashData(
                CanonicalJson.SerializeObject(
                    await new BundleSnapshotBuilder(db2).BuildAsync(Now)));
        }

        secondHash.Should().Equal(
            firstHash,
            "snapshot determinism is the load-bearing invariant for offline " +
            "verifiers; the same catalog state must always yield the same hash");
    }
}
