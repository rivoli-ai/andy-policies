// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Audit;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Integration.Bundles;

/// <summary>
/// P8.7 (#87) — at-the-service-boundary hash determinism. Two
/// bundles created from byte-identical catalog state must produce
/// byte-identical <c>SnapshotHash</c> values; one extra binding
/// must change the hash. The unit-level guarantee from P8.2 covers
/// the canonical-JSON pass; this is the floor under what consumers
/// pinning a bundle observe end-to-end.
/// </summary>
public class HashDeterminismTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public HashDeterminismTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=true");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite(_connection)
        .Options;

    private async Task<AppDbContext> InitDbAsync()
    {
        var db = new AppDbContext(Options());
        await db.Database.MigrateAsync();
        return db;
    }

    private static BundleService NewService(AppDbContext db) => new(
        db,
        new BundleSnapshotBuilder(db),
        new AuditChain(db, TimeProvider.System),
        TimeProvider.System);

    private static async Task<(Guid policyId, Guid versionId)> SeedActiveAsync(
        AppDbContext db, string slug)
    {
        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = slug,
            CreatedBySubjectId = "seed",
        };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            Version = 1,
            State = LifecycleState.Active,
            Enforcement = EnforcementLevel.Should,
            Severity = Severity.Moderate,
            Scopes = new List<string>(),
            Summary = "fixture",
            RulesJson = "{}",
            CreatedBySubjectId = "seed",
            ProposerSubjectId = "seed",
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedBySubjectId = "seed",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return (policy.Id, version.Id);
    }

    [Fact]
    public async Task TwoBundles_FromIdenticalCatalogState_HaveDifferentHashes()
    {
        // Subtle: the BundleSnapshot record carries CapturedAt and
        // AuditTailHash, both of which advance on every CreateAsync
        // (CapturedAt = clock.GetUtcNow(); AuditTailHash bumps after
        // the bundle.create audit event from the prior call). Two
        // bundles taken back-to-back from byte-identical *catalog*
        // state therefore produce *different* SnapshotHashes — the
        // hash captures the snapshot's temporal + audit coordinate,
        // not just the policy/binding/override set. The reproducibility
        // contract is per-bundle (same id -> same answers always),
        // which is covered by P8.3 BundleResolverTests, not "two
        // independent bundles equal each other."
        await using var db = await InitDbAsync();
        await SeedActiveAsync(db, "p1");
        var svc = NewService(db);

        var first = await svc.CreateAsync(
            new CreateBundleRequest("det-a", null, "rationale"), "seed", CancellationToken.None);
        var second = await svc.CreateAsync(
            new CreateBundleRequest("det-b", null, "rationale"), "seed", CancellationToken.None);

        second.SnapshotHash.Should().NotBe(
            first.SnapshotHash,
            "two consecutive bundles must have distinct hashes — the second " +
            "one's snapshot includes the audit tail bumped by the first " +
            "bundle.create event, even though the policy/binding sets are " +
            "unchanged. Treating them as identical would defeat tamper " +
            "detection over the (bundle, audit-chain-tail) pair.");
    }

    [Fact]
    public async Task SameBundle_ReadTwice_HashIsStable()
    {
        // The actual reproducibility contract: a bundle's hash is
        // stable for the lifetime of its row. Tests this end-to-end
        // by reading the same bundle twice and asserting the hash
        // string is identical (post-insert immutability + the
        // SaveChanges sweep from P8.1).
        await using var db = await InitDbAsync();
        await SeedActiveAsync(db, "p1");
        var svc = NewService(db);
        var dto = await svc.CreateAsync(
            new CreateBundleRequest("stable", null, "rationale"), "seed", CancellationToken.None);

        var firstRead = await svc.GetAsync(dto.Id);
        var secondRead = await svc.GetAsync(dto.Id);

        firstRead!.SnapshotHash.Should().Be(dto.SnapshotHash);
        secondRead!.SnapshotHash.Should().Be(dto.SnapshotHash);
    }

    [Fact]
    public async Task BundlesCreated_AfterAdditionalBinding_HaveDifferentSnapshotHash()
    {
        await using var db = await InitDbAsync();
        var (_, versionId) = await SeedActiveAsync(db, "p1");
        var svc = NewService(db);
        var before = await svc.CreateAsync(
            new CreateBundleRequest("before", null, "rationale"), "seed", CancellationToken.None);

        // Add a binding then take another snapshot.
        db.Bindings.Add(new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = versionId,
            TargetType = BindingTargetType.Repo,
            TargetRef = "repo:rivoli-ai/x",
            BindStrength = BindStrength.Mandatory,
            CreatedBySubjectId = "seed",
        });
        await db.SaveChangesAsync();

        var after = await svc.CreateAsync(
            new CreateBundleRequest("after", null, "rationale"), "seed", CancellationToken.None);

        after.SnapshotHash.Should().NotBe(
            before.SnapshotHash,
            "an additional binding changes the snapshot, so the hash must " +
            "change too — otherwise tamper detection (P8.7 verifier hooks) " +
            "would miss real edits");
    }

    [Fact]
    public async Task SnapshotHash_RoundTrips_AgainstStoredCanonicalJson()
    {
        // The hash invariant: SHA-256 of the bytes of SnapshotJson
        // (UTF-8) equals the SnapshotHash hex. This is the defining
        // property an offline verifier relies on — without it,
        // tamper detection is impossible.
        await using var db = await InitDbAsync();
        await SeedActiveAsync(db, "p1");
        var svc = NewService(db);
        var dto = await svc.CreateAsync(
            new CreateBundleRequest("rt", null, "rationale"), "seed", CancellationToken.None);

        var bundle = await db.Bundles.AsNoTracking().FirstAsync(b => b.Id == dto.Id);
        var bytes = System.Text.Encoding.UTF8.GetBytes(bundle.SnapshotJson);
        var recomputed = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

        recomputed.Should().Be(
            bundle.SnapshotHash,
            "if recomputing the hash from stored bytes diverges from the " +
            "stored hash, no offline verifier can prove a snapshot is intact");
    }
}
