// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Andy.Policies.Tests.Integration.Embedded;

/// <summary>
/// P10.1 (#31): every committed EF migration must apply cleanly
/// against an in-memory SQLite connection via
/// <see cref="DatabaseFacade.MigrateAsync"/>. This is the
/// embedded-mode (Conductor bundled deployment) boot contract: the
/// shared migration set survives the round-trip through the SQLite
/// provider, populates <c>__EFMigrationsHistory</c>, and
/// <see cref="DatabaseFacade.GetAppliedMigrationsAsync"/> returns
/// the full ordered set after apply.
/// </summary>
public class SqliteMigrationApplyTests
{
    [Fact]
    public async Task EveryMigration_AppliesCleanly_OnSqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);

        await db.Database.MigrateAsync();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var defined = db.Database.GetMigrations().ToList();

        applied.Should().BeEquivalentTo(
            defined,
            "every migration in the source tree must apply on SQLite; an " +
            "embedded-mode boot reads the same migration set and would fail " +
            "if any migration carried Postgres-only DDL. The set we apply " +
            "must equal the set we know about — no skipped, no extras.");
    }

    [Fact]
    public async Task MigrationApply_IsIdempotent_OnSecondRun()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);

        await db.Database.MigrateAsync();
        var firstCount = (await db.Database.GetAppliedMigrationsAsync()).Count();

        // Second migrate against the same connection should be a
        // no-op — Conductor operators restart containers; without
        // idempotency they'd hit "table X already exists" on every
        // restart against an existing sqlite_data volume.
        await db.Database.MigrateAsync();
        var secondCount = (await db.Database.GetAppliedMigrationsAsync()).Count();

        secondCount.Should().Be(firstCount,
            "MigrateAsync must be safe to call on every boot — it's the " +
            "default startup path for embedded mode now (P10.1)");
    }

    [Fact]
    public async Task MigrationsHistoryTable_IsPopulated_AfterApply()
    {
        // EnsureCreated would skip the history table; MigrateAsync
        // populates it. A history table is the precondition for
        // upgrade-in-place against an existing sqlite_data volume.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);

        await db.Database.MigrateAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
        var historyTable = (string?)await cmd.ExecuteScalarAsync();
        historyTable.Should().Be(
            "__EFMigrationsHistory",
            "the history table is the difference between MigrateAsync (allows " +
            "upgrade) and EnsureCreated (one-shot init); operators upgrading " +
            "the embedded image to a newer release require this table to exist");
    }
}
