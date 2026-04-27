// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Andy.Policies.Tests.Integration.Migration;

/// <summary>
/// P1.11 (#91): EF Core migrations must apply cleanly on Postgres — the
/// production provider. Existing tests use SQLite-backed factories for speed;
/// this fixture spins up a real ephemeral Postgres via Testcontainers so the
/// Npgsql-specific bits (jsonb columns, text[] arrays, partial unique indexes)
/// are exercised end-to-end.
///
/// Skipped silently when the Docker daemon is unavailable so contributor
/// laptops without Docker don't fail the suite. CI runners (ubuntu-latest)
/// have Docker by default.
/// </summary>
public class PostgresMigrationTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("andy_policies_test")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
            _dockerAvailable = true;
        }
        catch (Exception)
        {
            // Docker unavailable on this host: tests below short-circuit. We
            // intentionally swallow rather than fail the fixture so a
            // contributor without Docker still gets a green Sqlite suite.
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

    private DbContextOptions<AppDbContext> NewOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

    [SkippableFact]
    public async Task MigrateAsync_OnEmptyPostgres_AppliesCleanly()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(NewOptions());

        await db.Database.MigrateAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(new[]
        {
            "20260422024314_InitialPolicyCatalog",
            "20260422031628_AddPolicyDimensions",
            "20260427000816_AddRetiredAtToPolicyVersion",
        });
    }

    [SkippableFact]
    public async Task MigrateAsync_AppliedTwice_SecondCallIsNoOp()
    {
        Skip.IfNot(_dockerAvailable);

        await using (var first = new AppDbContext(NewOptions()))
        {
            await first.Database.MigrateAsync();
        }

        await using var second = new AppDbContext(NewOptions());
        var pendingBefore = await second.Database.GetPendingMigrationsAsync();
        pendingBefore.Should().BeEmpty();

        await second.Database.MigrateAsync();

        var pendingAfter = await second.Database.GetPendingMigrationsAsync();
        pendingAfter.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task MigratedSchema_UsesNativeTextArrayForScopes()
    {
        // The Sqlite provider stores scopes as a delimited string via a value
        // converter; Postgres uses native text[]. This assertion guards the
        // provider-specific column type configuration in AppDbContext.
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        var dataType = (await db.Database.SqlQueryRaw<string>(
                """
                SELECT data_type
                FROM information_schema.columns
                WHERE table_name = 'policy_versions' AND column_name = 'Scopes'
                """).ToListAsync()).Single();

        dataType.Should().Be("ARRAY");
    }

    [SkippableFact]
    public async Task MigratedSchema_RulesJsonStoredAsJsonb()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        var udtName = (await db.Database.SqlQueryRaw<string>(
                """
                SELECT udt_name
                FROM information_schema.columns
                WHERE table_name = 'policy_versions' AND column_name = 'RulesJson'
                """).ToListAsync()).Single();

        udtName.Should().Be("jsonb");
    }

    [SkippableFact]
    public async Task MigratedSchema_HasOneDraftAndOneActivePartialIndex()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        var indexNames = await db.Database.SqlQueryRaw<string>(
                """
                SELECT indexname
                FROM pg_indexes
                WHERE tablename = 'policy_versions'
                """).ToListAsync();

        indexNames.Should().Contain(new[]
        {
            "ix_policy_versions_one_draft_per_policy",
            "ix_policy_versions_one_active_per_policy",
        });
    }
}
