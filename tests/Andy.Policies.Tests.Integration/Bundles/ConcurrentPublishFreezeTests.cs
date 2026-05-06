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
/// P8.7 (#87) — the bundle reproducibility contract under
/// post-create catalog mutation. The serializable transaction in
/// <see cref="BundleService.CreateAsync"/> already guarantees that
/// a snapshot taken at <c>T</c> sees only state committed before
/// <c>T</c> (P8.2 #82); this fixture proves the same end-to-end
/// at the service-boundary by mutating the catalog after the
/// bundle insert and asserting the snapshot is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not literal concurrent transactions?</b> SQLite's
/// in-memory mode serialises writers behind a process-wide latch,
/// and EF Core's transaction APIs against an InMemory provider are
/// no-ops. Modelling the post-race state as "publish a new
/// version after the bundle is created and confirm the bundle
/// didn't see it" is the same invariant from a consumer's
/// perspective: pinning is reproducible across catalog churn. The
/// real serializable-transaction race is exercised by the P8.2
/// Postgres testcontainer suite when Docker is available
/// (<c>BundleServiceTests.Create_RetriesOnSerializationFailure</c>).
/// </para>
/// </remarks>
public class ConcurrentPublishFreezeTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public ConcurrentPublishFreezeTests()
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

    [Fact]
    public async Task BundleSnapshot_DoesNotLeak_PolicyVersionPublished_AfterCreate()
    {
        await using var db = await InitDbAsync();
        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = "freeze-test",
            CreatedBySubjectId = "seed",
        };
        var v1 = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            Version = 1,
            State = LifecycleState.Active,
            Enforcement = EnforcementLevel.Should,
            Severity = Severity.Moderate,
            Scopes = new List<string>(),
            Summary = "v1",
            RulesJson = "{}",
            CreatedBySubjectId = "seed",
            ProposerSubjectId = "seed",
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedBySubjectId = "seed",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(v1);
        await db.SaveChangesAsync();

        var svc = new BundleService(
            db,
            new BundleSnapshotBuilder(db),
            new AuditChain(db, TimeProvider.System),
            TimeProvider.System);
        var bundle = await svc.CreateAsync(
            new CreateBundleRequest("freeze", null, "race"), "seed", CancellationToken.None);

        // Now retire v1 and publish v2 — exactly the catalog churn
        // a pinning consumer must be insulated from.
        v1.State = LifecycleState.Retired;
        v1.RetiredAt = DateTimeOffset.UtcNow;
        var v2 = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            Version = 2,
            State = LifecycleState.Active,
            Enforcement = EnforcementLevel.Must,
            Severity = Severity.Critical,
            Scopes = new List<string>(),
            Summary = "v2",
            RulesJson = "{}",
            CreatedBySubjectId = "seed",
            ProposerSubjectId = "seed",
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedBySubjectId = "seed",
        };
        db.PolicyVersions.Add(v2);
        await db.SaveChangesAsync();

        // Resolve via the resolver — which only reads the bundle's
        // SnapshotJson, not live state. v1 must still appear.
        var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var resolver = new BundleResolver(db, memoryCache);
        var pinned = await resolver.GetPinnedPolicyAsync(bundle.Id, policy.Id);

        pinned.Should().NotBeNull();
        pinned!.PolicyVersionId.Should().Be(
            v1.Id,
            "the bundle was created when v1 was Active; a subsequent retire+publish " +
            "must not be visible through the bundle, otherwise pinning provides no " +
            "reproducibility");
        pinned.VersionNumber.Should().Be(1);
        pinned.Enforcement.Should().Be("SHOULD");
    }

    [Fact]
    public async Task BundleSnapshot_DoesNotLeak_BindingAdded_AfterCreate()
    {
        await using var db = await InitDbAsync();
        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = "freeze-binding",
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

        var svc = new BundleService(
            db,
            new BundleSnapshotBuilder(db),
            new AuditChain(db, TimeProvider.System),
            TimeProvider.System);
        var bundle = await svc.CreateAsync(
            new CreateBundleRequest("freeze-bind", null, "race"), "seed", CancellationToken.None);

        // Add a binding after the bundle insert.
        db.Bindings.Add(new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = version.Id,
            TargetType = BindingTargetType.Repo,
            TargetRef = "repo:post-race",
            BindStrength = BindStrength.Mandatory,
            CreatedBySubjectId = "seed",
        });
        await db.SaveChangesAsync();

        var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var resolver = new BundleResolver(db, memoryCache);
        var result = await resolver.ResolveAsync(bundle.Id, BindingTargetType.Repo, "repo:post-race");

        result.Should().NotBeNull();
        result!.Bindings.Should().BeEmpty(
            "the binding was added after the snapshot; pinning must not see it, " +
            "otherwise consumers using the same bundle id at different times would " +
            "get different answers");
    }
}
