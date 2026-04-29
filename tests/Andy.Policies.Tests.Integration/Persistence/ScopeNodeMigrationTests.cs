// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Integration.Persistence;

/// <summary>
/// P4.1 (#28) integration tests for the <see cref="ScopeNode"/> table:
/// migration applies cleanly on Sqlite, the unique <c>(Type, Ref)</c>
/// constraint rejects duplicates, the FK Restrict rejects child inserts
/// against a missing parent, and the three indexes
/// (<c>ix_scope_nodes_type_ref</c>, <c>_parent_id</c>, <c>_materialized_path</c>)
/// are physically present so P4.3 hierarchy walks can rely on the
/// covering paths.
/// </summary>
public class ScopeNodeMigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;

    public ScopeNodeMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"andy-policies-scope-{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath};Foreign Keys=true");
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private DbContextOptions<AppDbContext> NewOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

    [Fact]
    public async Task Migration_CreatesScopeNodesTable_WithExpectedIndexes()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        var indexes = await db.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='scope_nodes'")
            .ToListAsync();

        indexes.Should().Contain(new[]
        {
            "ix_scope_nodes_type_ref",
            "ix_scope_nodes_parent_id",
            "ix_scope_nodes_materialized_path",
        });
    }

    [Fact]
    public async Task Insert_RootOrgNode_RoundTripsAllFields()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();
        var rootId = Guid.NewGuid();

        var root = new ScopeNode
        {
            Id = rootId,
            ParentId = null,
            Type = ScopeType.Org,
            Ref = "org:test",
            DisplayName = "Test Org",
            MaterializedPath = $"/{rootId}",
            Depth = 0,
        };
        db.ScopeNodes.Add(root);
        await db.SaveChangesAsync();

        var reloaded = await db.ScopeNodes.AsNoTracking().FirstAsync(s => s.Id == rootId);
        reloaded.ParentId.Should().BeNull();
        reloaded.Type.Should().Be(ScopeType.Org);
        reloaded.Ref.Should().Be("org:test");
        reloaded.DisplayName.Should().Be("Test Org");
        reloaded.MaterializedPath.Should().Be($"/{rootId}");
        reloaded.Depth.Should().Be(0);
    }

    [Fact]
    public async Task Insert_DuplicateTypeRefPair_ThrowsDbUpdateException()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();
        var firstId = Guid.NewGuid();
        db.ScopeNodes.Add(new ScopeNode
        {
            Id = firstId,
            Type = ScopeType.Tenant,
            Ref = "tenant:dup",
            DisplayName = "First",
            MaterializedPath = $"/{firstId}",
            Depth = 1,
        });
        await db.SaveChangesAsync();

        db.ScopeNodes.Add(new ScopeNode
        {
            Type = ScopeType.Tenant,
            Ref = "tenant:dup",  // same (Type, Ref) pair as the first row
            DisplayName = "Second",
            MaterializedPath = "/duplicate",
            Depth = 1,
        });

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Insert_RepeatedRef_DifferentTypes_BothPersist()
    {
        // The composite unique index permits the same Ref under different
        // TargetType values. Documents the boundary explicitly so a
        // future "stricter" change is a deliberate decision, not an
        // accidental one.
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        var teamId = Guid.NewGuid();
        var repoId = Guid.NewGuid();
        db.ScopeNodes.Add(new ScopeNode
        {
            Id = teamId,
            Type = ScopeType.Team,
            Ref = "shared",
            DisplayName = "Team-shared",
            MaterializedPath = $"/{teamId}",
            Depth = 2,
        });
        db.ScopeNodes.Add(new ScopeNode
        {
            Id = repoId,
            Type = ScopeType.Repo,
            Ref = "shared",
            DisplayName = "Repo-shared",
            MaterializedPath = $"/{repoId}",
            Depth = 3,
        });
        await db.SaveChangesAsync();

        (await db.ScopeNodes.AsNoTracking().CountAsync(s => s.Ref == "shared")).Should().Be(2);
    }

    [Fact]
    public async Task Insert_ChildWithUnknownParent_ThrowsDbUpdateException()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        db.ScopeNodes.Add(new ScopeNode
        {
            ParentId = Guid.NewGuid(),  // never seeded
            Type = ScopeType.Tenant,
            Ref = "tenant:orphan",
            DisplayName = "Orphan",
            MaterializedPath = "/orphan",
            Depth = 1,
        });

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Delete_ParentWithChildren_IsRejected_ByFkRestrict()
    {
        // Seed the parent + child rows in a fresh context, then issue a
        // raw DELETE through ExecuteSqlRaw so EF's change-tracker doesn't
        // try to cascade-clear the child's ParentId before the DELETE
        // hits the wire (which would defeat the test). The DB-level FK
        // Restrict is what we're proving here.
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        db.ScopeNodes.Add(new ScopeNode
        {
            Id = parentId,
            Type = ScopeType.Org,
            Ref = "org:parent",
            DisplayName = "Parent",
            MaterializedPath = $"/{parentId}",
            Depth = 0,
        });
        db.ScopeNodes.Add(new ScopeNode
        {
            Id = childId,
            ParentId = parentId,
            Type = ScopeType.Tenant,
            Ref = "tenant:child",
            DisplayName = "Child",
            MaterializedPath = $"/{parentId}/{childId}",
            Depth = 1,
        });
        await db.SaveChangesAsync();

        var act = async () => await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM scope_nodes WHERE \"Id\" = {0}", parentId);

        // SqliteException wraps the FK-violation; SQLite doesn't surface
        // it as DbUpdateException because we're going around EF.
        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Fact]
    public async Task RootScope_Seeder_IsIdempotent_AndStampsKnownIdAndPath()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        await ScopeSeeder.SeedRootScopeAsync(db);
        await ScopeSeeder.SeedRootScopeAsync(db);  // second call must short-circuit

        var roots = await db.ScopeNodes.AsNoTracking()
            .Where(s => s.ParentId == null).ToListAsync();
        roots.Should().ContainSingle();
        var root = roots[0];
        root.Id.Should().Be(ScopeSeeder.RootOrgId);
        root.Type.Should().Be(ScopeType.Org);
        root.Ref.Should().Be(ScopeSeeder.RootOrgRef);
        root.MaterializedPath.Should().Be($"/{ScopeSeeder.RootOrgId}");
        root.Depth.Should().Be(0);
    }
}
