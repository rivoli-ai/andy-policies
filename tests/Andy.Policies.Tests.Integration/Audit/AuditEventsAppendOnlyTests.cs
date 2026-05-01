// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Andy.Policies.Tests.Integration.Audit;

/// <summary>
/// P6.1 (#41) — verifies the append-only invariant on the
/// <c>audit_events</c> table is enforced by the database itself,
/// independent of application code:
/// <list type="bullet">
///   <item>UPDATE on any column raises an error.</item>
///   <item>DELETE raises an error.</item>
///   <item>INSERT + SELECT continue to work.</item>
/// </list>
/// Both providers in scope: Postgres (production), SQLite
/// (embedded mode). The Postgres legs are gated on Docker
/// availability so a contributor without Docker still sees a
/// green SQLite suite.
/// </summary>
public class AuditEventsAppendOnlyTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _pgConnectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("andy_policies_audit_test")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
            await _container.StartAsync();
            _pgConnectionString = _container.GetConnectionString();
            _dockerAvailable = true;
        }
        catch (Exception)
        {
            _dockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private DbContextOptions<AppDbContext> PostgresOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_pgConnectionString)
            .Options;

    private static DbContextOptions<AppDbContext> SqliteOptions(SqliteConnection conn) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .Options;

    [SkippableFact]
    public async Task Postgres_Insert_Succeeds()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(PostgresOptions());
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_pgConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_events
                ("Id", "PrevHash", "Hash", "Timestamp", "ActorSubjectId",
                 "ActorRoles", "Action", "EntityType", "EntityId",
                 "FieldDiffJson", "Rationale")
            VALUES
                (gen_random_uuid(),
                 decode('0000000000000000000000000000000000000000000000000000000000000000', 'hex'),
                 decode('1111111111111111111111111111111111111111111111111111111111111111', 'hex'),
                 NOW(),
                 'user:test', ARRAY['admin'], 'policy.create',
                 'Policy', '00000000-0000-0000-0000-000000000001',
                 '[]'::jsonb, 'integration test');
            """;
        var rows = await cmd.ExecuteNonQueryAsync();
        rows.Should().Be(1);
    }

    [SkippableFact]
    public async Task Postgres_Update_RaisesAppendOnlyError()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(PostgresOptions());
        await db.Database.MigrateAsync();

        // Seed a row first so UPDATE has a target.
        await using var conn = new NpgsqlConnection(_pgConnectionString);
        await conn.OpenAsync();
        await SeedRowPostgresAsync(conn);

        await using var update = conn.CreateCommand();
        update.CommandText = "UPDATE audit_events SET \"Rationale\" = 'tampered'";
        var act = async () => await update.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.MessageText.Should().Contain("audit_events is append-only");
    }

    [SkippableFact]
    public async Task Postgres_Delete_RaisesAppendOnlyError()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(PostgresOptions());
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_pgConnectionString);
        await conn.OpenAsync();
        await SeedRowPostgresAsync(conn);

        await using var delete = conn.CreateCommand();
        delete.CommandText = "DELETE FROM audit_events";
        var act = async () => await delete.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.MessageText.Should().Contain("audit_events is append-only");
    }

    [SkippableFact]
    public async Task Postgres_Truncate_RaisesAppendOnlyError()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(PostgresOptions());
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_pgConnectionString);
        await conn.OpenAsync();
        await SeedRowPostgresAsync(conn);

        await using var truncate = conn.CreateCommand();
        truncate.CommandText = "TRUNCATE TABLE audit_events";
        var act = async () => await truncate.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.MessageText.Should().Contain("audit_events is append-only");
    }

    [SkippableFact]
    public async Task Postgres_Down_ReverseCleanly()
    {
        // The Down() migration must drop the trigger + function
        // before dropping the table; otherwise rolling back this
        // migration on a populated database would trip the trigger.
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(PostgresOptions());
        await db.Database.MigrateAsync();

        // Seed a row so the table is non-empty when reversed.
        await using (var conn = new NpgsqlConnection(_pgConnectionString))
        {
            await conn.OpenAsync();
            await SeedRowPostgresAsync(conn);
        }

        // Roll back to the previous migration (AddOverrides).
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20260430232106_AddOverrides");

        // The table is gone; re-applying succeeds and we can insert again.
        await db.Database.MigrateAsync();
        await using var conn2 = new NpgsqlConnection(_pgConnectionString);
        await conn2.OpenAsync();
        await SeedRowPostgresAsync(conn2);
    }

    [Fact]
    public async Task Sqlite_Update_RaisesAppendOnlyError()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(SqliteOptions(connection));
        await db.Database.MigrateAsync();

        await SeedRowSqliteAsync(connection);

        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE audit_events SET \"Rationale\" = 'tampered'";
        var act = async () => await update.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<SqliteException>();
        ex.Which.Message.Should().Contain("audit_events is append-only");
    }

    [Fact]
    public async Task Sqlite_Delete_RaisesAppendOnlyError()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(SqliteOptions(connection));
        await db.Database.MigrateAsync();

        await SeedRowSqliteAsync(connection);

        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM audit_events";
        var act = async () => await delete.ExecuteNonQueryAsync();

        var ex = await act.Should().ThrowAsync<SqliteException>();
        ex.Which.Message.Should().Contain("audit_events is append-only");
    }

    [Fact]
    public async Task Sqlite_InsertAndSelect_Succeed()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(SqliteOptions(connection));
        await db.Database.MigrateAsync();

        await SeedRowSqliteAsync(connection);

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT COUNT(*) FROM audit_events";
        var count = (long)(await select.ExecuteScalarAsync())!;
        count.Should().Be(1);
    }

    private static async Task SeedRowPostgresAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_events
                ("Id", "PrevHash", "Hash", "Timestamp", "ActorSubjectId",
                 "ActorRoles", "Action", "EntityType", "EntityId",
                 "FieldDiffJson", "Rationale")
            VALUES
                (gen_random_uuid(),
                 decode('0000000000000000000000000000000000000000000000000000000000000000', 'hex'),
                 decode('1111111111111111111111111111111111111111111111111111111111111111', 'hex'),
                 NOW(),
                 'user:test', ARRAY['admin'], 'policy.create',
                 'Policy', '00000000-0000-0000-0000-000000000001',
                 '[]'::jsonb, 'integration test');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedRowSqliteAsync(SqliteConnection conn, long seq = 1)
    {
        // P6.2's chain writer will assign Seq monotonically (Postgres
        // bigserial / advisory-lock counter on SQLite). For this
        // integration test we supply it explicitly so the row insert
        // doesn't depend on the chain service yet.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_events
                ("Id", "seq", "PrevHash", "Hash", "Timestamp", "ActorSubjectId",
                 "ActorRoles", "Action", "EntityType", "EntityId",
                 "FieldDiffJson", "Rationale")
            VALUES
                ($id, $seq, $prev, $hash, $ts, 'user:test', 'admin',
                 'policy.create', 'Policy',
                 '00000000-0000-0000-0000-000000000001',
                 '[]', 'integration test');
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$prev", new byte[32]);
        var hash = new byte[32];
        Array.Fill(hash, (byte)0x11);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }
}
