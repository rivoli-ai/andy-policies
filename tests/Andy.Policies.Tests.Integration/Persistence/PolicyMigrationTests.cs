// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Integration.Persistence;

/// <summary>
/// SQLite-backed migration smoke tests for the InitialPolicyCatalog migration.
/// A Postgres-backed equivalent (Testcontainers.PostgreSql) is tracked by P1.11
/// (rivoli-ai/andy-policies#91) where the full cross-provider integration suite lands.
/// Shipping the SQLite path here satisfies the P1.1 acceptance criterion for
/// migration-applies-cleanly on one provider and proves the domain shape out of the gate.
/// </summary>
public class PolicyMigrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PolicyMigrationTests()
    {
        // Open connection lives for the duration of the test so EF's pooled open/close
        // cycles do not wipe the in-memory database.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Migration_AppliesCleanly_OnSqlite()
    {
        using var db = new AppDbContext(_options);

        await db.Database.MigrateAsync();

        // Spot-check the emitted schema: the policies + policy_versions tables plus both
        // partial unique indexes.
        using var tablesCmd = _connection.CreateCommand();
        tablesCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        using (var reader = await tablesCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        Assert.Contains("policies", tables);
        Assert.Contains("policy_versions", tables);

        using var indexesCmd = _connection.CreateCommand();
        indexesCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' ORDER BY name";
        var indexes = new List<string>();
        using (var reader = await indexesCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));
        }
        Assert.Contains("ix_policy_versions_one_draft_per_policy", indexes);
        Assert.Contains("ix_policy_versions_one_active_per_policy", indexes);
    }

    [Fact]
    public async Task Migration_IsIdempotent_WhenAppliedTwice()
    {
        using (var db = new AppDbContext(_options))
        {
            await db.Database.MigrateAsync();
        }

        using var countCmdAfterFirst = _connection.CreateCommand();
        countCmdAfterFirst.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"";
        var countAfterFirst = (long)(await countCmdAfterFirst.ExecuteScalarAsync() ?? 0L);

        // A second `MigrateAsync` on the same connection is a no-op — the migration history
        // table is consulted and no pending migrations are found.
        using (var db = new AppDbContext(_options))
        {
            await db.Database.MigrateAsync();
        }

        using var countCmdAfterSecond = _connection.CreateCommand();
        countCmdAfterSecond.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"";
        var countAfterSecond = (long)(await countCmdAfterSecond.ExecuteScalarAsync() ?? 0L);

        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    [Fact]
    public async Task CompositeUniqueIndex_OnPolicyIdAndVersion_RejectsDuplicate()
    {
        using var db = new AppDbContext(_options);
        await db.Database.MigrateAsync();

        var policy = new Policy { Id = Guid.NewGuid(), Name = "no-prod", CreatedBySubjectId = "u1" };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = LifecycleState.Draft,
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        });
        await db.SaveChangesAsync();

        db.PolicyVersions.Add(new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = LifecycleState.Retired,
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task OneDraftPerPolicy_PartialUniqueIndex_RejectsTwoDrafts()
    {
        using var db = new AppDbContext(_options);
        await db.Database.MigrateAsync();

        var policy = new Policy { Id = Guid.NewGuid(), Name = "high-risk", CreatedBySubjectId = "u1" };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = LifecycleState.Draft,
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        });
        await db.SaveChangesAsync();

        db.PolicyVersions.Add(new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 2,
            State = LifecycleState.Draft,
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
