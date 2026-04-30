// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Integration.Migration;

/// <summary>
/// P1.11 (#91): EF Core migrations must apply cleanly on Sqlite — the embedded
/// provider used by the Conductor bundling mode (Epic P10). Existing tests use
/// SQLite in-memory via <c>EnsureCreated</c> for speed; this fixture uses
/// <c>MigrateAsync</c> against a tempfile-backed DB so the actual migration
/// scripts (not just the model) are exercised.
/// </summary>
public class SqliteMigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;

    public SqliteMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"andy-policies-mig-{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private DbContextOptions<AppDbContext> NewOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

    [Fact]
    public async Task MigrateAsync_OnEmptyDatabase_AppliesCleanly()
    {
        await using var db = new AppDbContext(NewOptions());

        await db.Database.MigrateAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(new[]
        {
            "20260422024314_InitialPolicyCatalog",
            "20260422031628_AddPolicyDimensions",
            "20260427000816_AddRetiredAtToPolicyVersion",
        });
        applied.Should().Contain(name => name.EndsWith("_AddBindings"));
        applied.Should().Contain(name => name.EndsWith("_AddScopeNodes"));
        applied.Should().Contain(name => name.EndsWith("_AddOverrides"));
    }

    [Fact]
    public async Task MigrateAsync_AppliedTwice_IsNoOpOnSecondCall()
    {
        await using (var first = new AppDbContext(NewOptions()))
        {
            await first.Database.MigrateAsync();
        }

        await using var second = new AppDbContext(NewOptions());
        var beforeApplied = (await second.Database.GetAppliedMigrationsAsync()).ToList();
        var pendingBefore = (await second.Database.GetPendingMigrationsAsync()).ToList();
        pendingBefore.Should().BeEmpty();

        await second.Database.MigrateAsync();

        var afterApplied = (await second.Database.GetAppliedMigrationsAsync()).ToList();
        afterApplied.Should().BeEquivalentTo(beforeApplied);
    }

    [Fact]
    public async Task MigratedSchema_HasPoliciesAndPolicyVersionsTables()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        var tables = await db.Database.SqlQueryRaw<string>(
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'")
            .ToListAsync();

        tables.Should().Contain(new[] { "policies", "policy_versions", "bindings", "scope_nodes", "overrides" });
    }

    [Fact]
    public async Task MigratedSchema_EnforcesPartialUniqueIndexOnDraftPerPolicy()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        var indexes = await db.Database.SqlQueryRaw<string>(
                "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='policy_versions'")
            .ToListAsync();

        indexes.Should().Contain(new[]
        {
            "ix_policy_versions_one_draft_per_policy",
            "ix_policy_versions_one_active_per_policy",
        });
    }
}
