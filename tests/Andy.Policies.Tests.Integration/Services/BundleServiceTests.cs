// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using Andy.Policies.Application.Exceptions;
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

namespace Andy.Policies.Tests.Integration.Services;

/// <summary>
/// Integration tests for <see cref="BundleService"/> end-to-end against
/// SQLite + the real <see cref="AuditChain"/>. Pins the cross-cutting
/// invariants P8.2 (#82) introduces: hash round-trip, audit-chain
/// linkage, name-collision rejection, soft-delete state flip.
/// </summary>
public class BundleServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public BundleServiceTests()
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

    private static async Task<Guid> SeedActiveVersionAsync(AppDbContext db, string name)
    {
        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = name,
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
        return version.Id;
    }

    [Fact]
    public async Task Create_HappyPath_WritesBundle_AndAppendsBundleCreateAuditEvent()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewService(db);

        var dto = await svc.CreateAsync(
            new CreateBundleRequest("snap-1", "first", "initial release"),
            actorSubjectId: "user:author",
            CancellationToken.None);

        dto.Id.Should().NotBeEmpty();
        dto.Name.Should().Be("snap-1");
        dto.SnapshotHash.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
        dto.State.Should().Be("Active");

        var bundle = await db.Bundles.AsNoTracking().FirstAsync(b => b.Id == dto.Id);
        bundle.SnapshotJson.Should().Contain("\"SchemaVersion\":\"1\"");

        var auditRow = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == "bundle.create" && e.EntityId == dto.Id.ToString())
            .SingleAsync();
        auditRow.Rationale.Should().Be("initial release");
        auditRow.ActorSubjectId.Should().Be("user:author");
        auditRow.FieldDiffJson.Should().Contain(dto.SnapshotHash,
            "the bundle.create event payload references the snapshot hash so " +
            "auditors can pivot bundle ↔ chain in either direction");
    }

    [Fact]
    public async Task Create_StoredSnapshotJson_HashesToSnapshotHash()
    {
        // The reproducibility contract: an offline verifier reading
        // SnapshotJson and SHA-256-ing the UTF-8 bytes must match
        // SnapshotHash. P8.2 promises this via canonical-JSON output;
        // this test pins the round-trip.
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewService(db);

        var dto = await svc.CreateAsync(
            new CreateBundleRequest("hash-rt", null, "rationale"),
            "user:rt", CancellationToken.None);

        var bundle = await db.Bundles.AsNoTracking().FirstAsync(b => b.Id == dto.Id);
        var bytes = Encoding.UTF8.GetBytes(bundle.SnapshotJson);
        var recomputed = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        recomputed.Should().Be(
            bundle.SnapshotHash,
            "if the canonical bytes that were hashed at insert-time differ from " +
            "the bytes stored, no offline verifier can reproduce the hash");
    }

    [Fact]
    public async Task Create_NameCollisionWithActiveBundle_ThrowsConflictException()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewService(db);

        await svc.CreateAsync(new CreateBundleRequest("dup", null, "first"),
            "user:a", CancellationToken.None);

        var act = async () => await svc.CreateAsync(
            new CreateBundleRequest("dup", null, "second"),
            "user:b", CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>()
            .WithMessage("*dup*already in use*");
    }

    [Fact]
    public async Task Create_AfterSoftDelete_AllowsNameReuse()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewService(db);

        var first = await svc.CreateAsync(new CreateBundleRequest("reuse", null, "v1"),
            "user:a", CancellationToken.None);
        await svc.SoftDeleteAsync(first.Id, "user:op", "rotate", CancellationToken.None);

        var second = await svc.CreateAsync(new CreateBundleRequest("reuse", null, "v2"),
            "user:b", CancellationToken.None);

        second.Id.Should().NotBe(first.Id);
        second.State.Should().Be("Active");
    }

    [Fact]
    public async Task SoftDelete_FlipsStateAndStampsTombstone_AndAppendsAuditEvent()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewService(db);
        var dto = await svc.CreateAsync(
            new CreateBundleRequest("doomed", null, "initial"),
            "user:a", CancellationToken.None);

        var deleted = await svc.SoftDeleteAsync(dto.Id, "user:op", "decommission", CancellationToken.None);

        deleted.Should().BeTrue();
        var reloaded = await db.Bundles.AsNoTracking().FirstAsync(b => b.Id == dto.Id);
        reloaded.State.Should().Be(BundleState.Deleted);
        reloaded.DeletedBySubjectId.Should().Be("user:op");
        reloaded.DeletedAt.Should().NotBeNull();

        var auditRow = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == "bundle.delete" && e.EntityId == dto.Id.ToString());
        auditRow.Rationale.Should().Be("decommission");
    }

    [Fact]
    public async Task SoftDelete_OnAlreadyDeletedBundle_ReturnsFalse_NoAuditAppend()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewService(db);
        var dto = await svc.CreateAsync(new CreateBundleRequest("twice", null, "x"),
            "user:a", CancellationToken.None);
        await svc.SoftDeleteAsync(dto.Id, "user:op", "first delete", CancellationToken.None);

        var second = await svc.SoftDeleteAsync(dto.Id, "user:op", "again", CancellationToken.None);

        second.Should().BeFalse();
        var deleteEvents = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == "bundle.delete" && e.EntityId == dto.Id.ToString())
            .CountAsync();
        deleteEvents.Should().Be(
            1,
            "the second delete is a no-op; emitting a duplicate audit event " +
            "would inflate the chain with non-events");
    }

    [Fact]
    public async Task SoftDelete_OnUnknownBundle_ReturnsFalse()
    {
        await using var db = await InitDbAsync();
        var svc = NewService(db);

        var result = await svc.SoftDeleteAsync(
            Guid.NewGuid(), "user:op", "doesn't exist", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_ReturnsActiveOnly_ByDefault()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewService(db);
        var live = await svc.CreateAsync(new CreateBundleRequest("live", null, "x"),
            "user:a", CancellationToken.None);
        var doomed = await svc.CreateAsync(new CreateBundleRequest("doomed", null, "x"),
            "user:a", CancellationToken.None);
        await svc.SoftDeleteAsync(doomed.Id, "user:op", "tombstone", CancellationToken.None);

        var listed = await svc.ListAsync(new ListBundlesFilter(), CancellationToken.None);

        listed.Select(b => b.Id).Should().Contain(live.Id);
        listed.Select(b => b.Id).Should().NotContain(doomed.Id);
    }

    [Fact]
    public async Task ListAsync_WithIncludeDeleted_ReturnsBoth()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewService(db);
        var live = await svc.CreateAsync(new CreateBundleRequest("live", null, "x"),
            "user:a", CancellationToken.None);
        var doomed = await svc.CreateAsync(new CreateBundleRequest("doomed", null, "x"),
            "user:a", CancellationToken.None);
        await svc.SoftDeleteAsync(doomed.Id, "user:op", "tombstone", CancellationToken.None);

        var listed = await svc.ListAsync(new ListBundlesFilter(IncludeDeleted: true), CancellationToken.None);

        listed.Select(b => b.Id).Should().Contain(new[] { live.Id, doomed.Id });
    }

    [Fact]
    public async Task GetAsync_ReturnsRow_AndNullForUnknown()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewService(db);
        var dto = await svc.CreateAsync(new CreateBundleRequest("getme", null, "x"),
            "user:a", CancellationToken.None);

        var hit = await svc.GetAsync(dto.Id, CancellationToken.None);
        var miss = await svc.GetAsync(Guid.NewGuid(), CancellationToken.None);

        hit.Should().NotBeNull();
        hit!.Id.Should().Be(dto.Id);
        miss.Should().BeNull();
    }

    [Fact]
    public async Task Create_AuditEventCarriesAuditTailHash_FromBeforeBundleInsert()
    {
        // The bundle.create event should carry the chain tail hash that
        // existed BEFORE the bundle insert appended its own row. P8
        // verifiers walk back from this tail hash to find the snapshot's
        // historical context.
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        // Pre-seed an audit event so the tail-hash is non-zero at
        // bundle-insert time. AuditChain.AppendAsync stamps the chain.
        var chain = new AuditChain(db, TimeProvider.System);
        await chain.AppendAsync(new AuditAppendRequest(
            Action: "policy.publish",
            EntityType: "Policy",
            EntityId: Guid.NewGuid().ToString(),
            FieldDiffJson: "[]",
            Rationale: "seed",
            ActorSubjectId: "user:seed",
            ActorRoles: Array.Empty<string>()), CancellationToken.None);
        var precedingTailHash = await db.AuditEvents.AsNoTracking()
            .OrderByDescending(e => e.Seq).Select(e => e.Hash).FirstAsync();

        var svc = NewService(db);
        var dto = await svc.CreateAsync(new CreateBundleRequest("audited", null, "x"),
            "user:a", CancellationToken.None);

        var auditRow = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == "bundle.create" && e.EntityId == dto.Id.ToString());
        var precedingHex = Convert.ToHexString(precedingTailHash).ToLowerInvariant();
        auditRow.FieldDiffJson.Should().Contain(
            precedingHex,
            "the bundle.create event embeds the audit-tail-hash captured at " +
            "snapshot time, which is the chain coordinate just before this " +
            "very event was appended");
    }
}
