// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Infrastructure.Audit;
using Andy.Policies.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Andy.Policies.Tests.Integration.Audit;

/// <summary>
/// P6.2 (#42) — exercises the live <see cref="AuditChain"/> against
/// SQLite + Postgres testcontainer. Three scenarios:
/// <list type="bullet">
///   <item>Append 100 events and verify the chain succeeds end-to-end.</item>
///   <item>Append 20 events concurrently — Seq must be contiguous,
///     no gaps, and the chain must verify (advisory lock /
///     SemaphoreSlim guarantees the FIFO).</item>
///   <item>Tamper detection: bypass the app and mutate one row's
///     hash byte; <see cref="IAuditChain.VerifyChainAsync"/>
///     returns <c>Valid=false</c> with the row's <c>Seq</c> as
///     <c>FirstDivergenceSeq</c>.</item>
/// </list>
/// Postgres legs are gated on Docker availability so contributor
/// laptops without Docker still see a green SQLite suite.
/// </summary>
public class AuditChainTests : IAsyncLifetime
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
                .WithDatabase("andy_policies_audit_chain")
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

    private DbContextOptions<AppDbContext> PgOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_pgConnectionString)
            .Options;

    private static DbContextOptions<AppDbContext> SqliteOptions(SqliteConnection conn) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .Options;

    private static AuditAppendRequest SampleRequest(int n) => new(
        Action: "policy.update",
        EntityType: "Policy",
        EntityId: $"00000000-0000-0000-0000-{n:D12}",
        FieldDiffJson: $"[{{\"op\":\"replace\",\"path\":\"/n\",\"value\":{n}}}]",
        Rationale: $"event #{n}",
        ActorSubjectId: "user:test",
        ActorRoles: new[] { "admin" });

    [Fact]
    public async Task Sqlite_AppendHundredEvents_VerifyChainSucceeds()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(SqliteOptions(connection));
        await db.Database.MigrateAsync();
        var chain = new AuditChain(db, TimeProvider.System);

        for (var i = 1; i <= 100; i++)
        {
            await chain.AppendAsync(SampleRequest(i), CancellationToken.None);
        }

        var result = await chain.VerifyChainAsync(null, null, CancellationToken.None);
        result.Valid.Should().BeTrue();
        result.FirstDivergenceSeq.Should().BeNull();
        result.InspectedCount.Should().Be(100);
        result.LastSeq.Should().Be(100);
    }

    [Fact]
    public async Task Sqlite_TamperOneRow_VerifyReturnsFirstDivergenceSeq()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(SqliteOptions(connection));
        await db.Database.MigrateAsync();
        var chain = new AuditChain(db, TimeProvider.System);

        for (var i = 1; i <= 10; i++)
        {
            await chain.AppendAsync(SampleRequest(i), CancellationToken.None);
        }

        // Bypass the app: drop the trigger, mutate row 5's hash,
        // recreate the trigger so subsequent runs of the test
        // matrix find the chain in the trigger-protected state.
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DROP TRIGGER IF EXISTS trg_audit_events_no_update;";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = connection.CreateCommand())
        {
            // XOR a single byte of hash[0] for the row at seq=5.
            cmd.CommandText = """
                UPDATE audit_events
                SET "Hash" = (
                    -- substr is 1-based; replace byte 1 with its NOT
                    SELECT (CHAR(0xFF) || substr("Hash", 2)) FROM audit_events WHERE "seq" = 5
                )
                WHERE "seq" = 5;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var result = await chain.VerifyChainAsync(null, null, CancellationToken.None);
        result.Valid.Should().BeFalse();
        result.FirstDivergenceSeq.Should().Be(5);
    }

    [Fact]
    public async Task Sqlite_PartialRangeVerify_SeedsPrevFromPriorRow()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(SqliteOptions(connection));
        await db.Database.MigrateAsync();
        var chain = new AuditChain(db, TimeProvider.System);

        for (var i = 1; i <= 10; i++)
        {
            await chain.AppendAsync(SampleRequest(i), CancellationToken.None);
        }

        var result = await chain.VerifyChainAsync(fromSeq: 6, toSeq: 8, CancellationToken.None);

        result.Valid.Should().BeTrue();
        result.InspectedCount.Should().Be(3);
        result.LastSeq.Should().Be(8);
    }

    [Fact]
    public async Task Sqlite_EmptyChain_VerifyReturnsValidWithZeroCounts()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(SqliteOptions(connection));
        await db.Database.MigrateAsync();
        var chain = new AuditChain(db, TimeProvider.System);

        var result = await chain.VerifyChainAsync(null, null, CancellationToken.None);

        result.Valid.Should().BeTrue();
        result.InspectedCount.Should().Be(0);
        result.LastSeq.Should().Be(0);
    }

    [SkippableFact]
    public async Task Postgres_AppendHundredEvents_VerifyChainSucceeds()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(PgOptions());
        await db.Database.MigrateAsync();
        var chain = new AuditChain(db, TimeProvider.System);

        for (var i = 1; i <= 100; i++)
        {
            await chain.AppendAsync(SampleRequest(i), CancellationToken.None);
        }

        var result = await chain.VerifyChainAsync(null, null, CancellationToken.None);
        result.Valid.Should().BeTrue();
        result.InspectedCount.Should().Be(100);
        result.LastSeq.Should().Be(100);
    }

    [SkippableFact]
    public async Task Postgres_ConcurrentAppends_AllSucceed_ChainStaysContiguous()
    {
        Skip.IfNot(_dockerAvailable);

        // Migrate once before launching the tasks so each
        // per-task DbContext finds the schema in place. Without
        // this, the migrator would race the writers.
        await using (var migrateDb = new AppDbContext(PgOptions()))
        {
            await migrateDb.Database.MigrateAsync();
        }

        // Each task uses its own DbContext (production pattern: scoped
        // per request). The advisory lock serialises them across
        // contexts; without it, two tasks would read the same tail
        // and produce two rows with the same prev_hash + seq=1, which
        // VerifyChain would surface as a divergence.
        const int concurrency = 20;
        var tasks = new List<Task>();
        for (var i = 0; i < concurrency; i++)
        {
            var n = i + 1;
            tasks.Add(Task.Run(async () =>
            {
                await using var db = new AppDbContext(PgOptions());
                var chain = new AuditChain(db, TimeProvider.System);
                await chain.AppendAsync(SampleRequest(n), CancellationToken.None);
            }));
        }
        await Task.WhenAll(tasks);

        // Verify exactly `concurrency` rows landed with seq 1..N
        // (no gaps, no duplicates).
        await using var verifyDb = new AppDbContext(PgOptions());
        var seqs = await verifyDb.AuditEvents.AsNoTracking()
            .OrderBy(e => e.Seq)
            .Select(e => e.Seq)
            .ToListAsync();
        seqs.Should().HaveCount(concurrency);
        seqs.Should().Equal(Enumerable.Range(1, concurrency).Select(i => (long)i));

        var chainResult = await new AuditChain(verifyDb, TimeProvider.System)
            .VerifyChainAsync(null, null, CancellationToken.None);
        chainResult.Valid.Should().BeTrue();
        chainResult.LastSeq.Should().Be(concurrency);
    }

    [SkippableFact]
    public async Task Postgres_TamperOneRow_VerifyReturnsFirstDivergenceSeq()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(PgOptions());
        await db.Database.MigrateAsync();
        var chain = new AuditChain(db, TimeProvider.System);

        for (var i = 1; i <= 10; i++)
        {
            await chain.AppendAsync(SampleRequest(i), CancellationToken.None);
        }

        // Drop the trigger to mutate a row, then recreate it.
        await using (var conn = new NpgsqlConnection(_pgConnectionString))
        {
            await conn.OpenAsync();
            await using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText = "DROP TRIGGER trg_audit_events_no_update ON audit_events";
            await dropCmd.ExecuteNonQueryAsync();
            await using var mutateCmd = conn.CreateCommand();
            mutateCmd.CommandText = """
                UPDATE audit_events
                SET "Hash" = (E'\\x' || repeat('AB', 32))::bytea
                WHERE "seq" = 5
                """;
            await mutateCmd.ExecuteNonQueryAsync();
        }

        var result = await chain.VerifyChainAsync(null, null, CancellationToken.None);
        result.Valid.Should().BeFalse();
        result.FirstDivergenceSeq.Should().Be(5);
    }
}
