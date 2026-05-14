// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Andy.Policies.Tests.Integration.Parity;

/// <summary>
/// SD4 (rivoli-ai/andy-policies#1171) — provider parity for the seed
/// path. The PR adds <see cref="PolicySeeder"/> (six lifecycle policies
/// in <see cref="LifecycleState.Active"/>) + <see cref="BindingSeeder"/>
/// (19 agent → policy bindings) + <see cref="BindingTargetType.Agent"/>
/// = 6. The seed wiring is provider-agnostic by construction
/// (<see cref="DatabaseExtensions.EnsureSeedDataAsync"/> drives a plain
/// <see cref="AppDbContext"/> with no provider branches), but the
/// jsonb/text/text[] column-type fork in
/// <see cref="AppDbContext.OnModelCreating"/> and the
/// <see cref="BindingTargetType"/> ordinal-to-int conversion both have
/// to survive a real round-trip through both providers.
/// <para>
/// Existing coverage:
/// <list type="bullet">
///   <item><see cref="Andy.Policies.Tests.Unit.Seed.PolicySeederTests"/>
///     and <see cref="Andy.Policies.Tests.Unit.Seed.BindingSeederTests"/>
///     use EF InMemory — no provider on either side.</item>
///   <item><see cref="Andy.Policies.Tests.Integration.Embedded.SqliteBootTests"/>
///     boots the API against SQLite but only asserts the 6 policies
///     land, not the 19 bindings nor the rules-json shape.</item>
///   <item><see cref="Andy.Policies.Tests.Integration.Migration.PostgresMigrationTests"/>
///     proves migrations apply on Postgres, but never runs the seeders.</item>
/// </list>
/// This fixture closes the gap by driving the full
/// migrate → seed → re-seed pipeline against both providers and
/// asserting the SD4 acceptance shape: six Active policies, nineteen
/// <see cref="BindingTargetType.Agent"/> bindings, the
/// <c>high-risk</c> approver chain intact in <see cref="PolicyVersion.RulesJson"/>,
/// and idempotent on second run.
/// </para>
/// <para>
/// The Postgres half is gated on Docker availability so contributor
/// laptops without Docker still get a green SQLite half; CI runners
/// (ubuntu-latest) have Docker by default — same posture as
/// <see cref="Andy.Policies.Tests.Integration.Migration.PostgresMigrationTests"/>.
/// </para>
/// </summary>
public class Sd4SeedProviderParityTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pgContainer;
    private string _pgConnectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _pgContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("andy_policies_sd4_parity")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
            await _pgContainer.StartAsync();
            _pgConnectionString = _pgContainer.GetConnectionString();
            _dockerAvailable = true;
        }
        catch (Exception)
        {
            // Docker unavailable: Postgres halves short-circuit via Skip.IfNot.
            _dockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_pgContainer is not null)
        {
            await _pgContainer.DisposeAsync();
        }
    }

    // ---- SQLite half ----

    private static async Task<(SqliteConnection Connection, AppDbContext Db)> NewSqliteAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var db = new AppDbContext(opts);
        await db.Database.MigrateAsync();
        return (conn, db);
    }

    [Fact]
    public async Task Sqlite_FreshBoot_LandsSixPoliciesAndNineteenBindings()
    {
        var (conn, db) = await NewSqliteAsync();
        await using var _c = conn;
        await using var _db = db;

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        (await db.Policies.CountAsync()).Should().Be(6);
        (await db.PolicyVersions.CountAsync(v => v.State == LifecycleState.Active))
            .Should().Be(6, "all six seeded versions land directly in Active state");
        (await db.Bindings.CountAsync(b => b.TargetType == BindingTargetType.Agent))
            .Should().Be(19, "SD4.2 fixture: 19 unique (agent, policy) edges");
    }

    [Fact]
    public async Task Sqlite_AgentTargetType_RoundTripsAsOrdinalSix()
    {
        // BindingTargetType.Agent = 6, persisted via HasConversion<int>.
        // SQLite is typeless on disk but the EF converter stamps the
        // ordinal — read-back through the converter must yield Agent.
        var (conn, db) = await NewSqliteAsync();
        await using var _c = conn;
        await using var _db = db;

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        // Read via EF: the converter rehydrates the enum.
        var sample = await db.Bindings.AsNoTracking().FirstAsync();
        sample.TargetType.Should().Be(BindingTargetType.Agent);

        // Read via raw SQL: the on-disk value is the literal ordinal 6.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """SELECT DISTINCT "TargetType" FROM bindings""";
        var raw = await cmd.ExecuteScalarAsync();
        Convert.ToInt32(raw).Should().Be(
            (int)BindingTargetType.Agent,
            "SD4.2 added Agent = 6; existing rows on disk depend on the stable ordinal");
    }

    [Fact]
    public async Task Sqlite_HighRiskRulesJson_RoundTripsApproverChain()
    {
        // PolicyVersion.RulesJson is mapped to TEXT on SQLite. The
        // high-risk policy carries the typed-confirmation approver chain
        // which is the load-bearing payload for Conductor's dangerous-
        // action confirmation prompts.
        var (conn, db) = await NewSqliteAsync();
        await using var _c = conn;
        await using var _db = db;

        await PolicySeeder.SeedStockPoliciesAsync(db);

        var highRisk = await db.Policies.AsNoTracking()
            .SingleAsync(p => p.Name == "high-risk");
        var version = await db.PolicyVersions.AsNoTracking()
            .SingleAsync(v => v.PolicyId == highRisk.Id);

        using var doc = JsonDocument.Parse(version.RulesJson);
        doc.RootElement.GetProperty("requireTypedConfirmation").GetBoolean().Should().BeTrue();
        var approvers = doc.RootElement.GetProperty("approvers");
        approvers.GetArrayLength().Should().Be(1);
        approvers[0].GetProperty("role").GetString().Should().Be("maintainer");
        approvers[0].GetProperty("minApprovals").GetInt32().Should().Be(1);
        approvers[0].GetProperty("selfApprovalForbidden").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Sqlite_Reseed_IsNoOp_NoDuplicates()
    {
        var (conn, db) = await NewSqliteAsync();
        await using var _c = conn;
        await using var _db = db;

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);
        var firstBundleCount = await db.Bundles.CountAsync();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);
        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        (await db.Policies.CountAsync()).Should().Be(6);
        (await db.PolicyVersions.CountAsync()).Should().Be(6);
        (await db.Bindings.CountAsync(b => b.TargetType == BindingTargetType.Agent))
            .Should().Be(19);
        (await db.Bundles.CountAsync()).Should().Be(
            firstBundleCount,
            "SD4 contract: reseed never bumps the bundle snapshot");
    }

    // ---- Postgres half (gated on Docker availability) ----

    private async Task<AppDbContext> NewPostgresAsync()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_pgConnectionString)
            .Options;
        var db = new AppDbContext(opts);
        await db.Database.MigrateAsync();
        return db;
    }

    [SkippableFact]
    public async Task Postgres_FreshBoot_LandsSixPoliciesAndNineteenBindings()
    {
        Skip.IfNot(_dockerAvailable);
        await using var db = await NewPostgresAsync();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        (await db.Policies.CountAsync()).Should().Be(6);
        (await db.PolicyVersions.CountAsync(v => v.State == LifecycleState.Active))
            .Should().Be(6);
        (await db.Bindings.CountAsync(b => b.TargetType == BindingTargetType.Agent))
            .Should().Be(19);
    }

    [SkippableFact]
    public async Task Postgres_AgentTargetType_RoundTripsAsOrdinalSix()
    {
        // BindingTargetType column is declared `integer` on Postgres;
        // HasConversion<int> writes the ordinal 6 for Agent. Round-trip
        // through both the EF converter and a raw SqlQueryRaw.
        Skip.IfNot(_dockerAvailable);
        await using var db = await NewPostgresAsync();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        var sample = await db.Bindings.AsNoTracking().FirstAsync();
        sample.TargetType.Should().Be(BindingTargetType.Agent);

        var rawOrdinals = await db.Database.SqlQueryRaw<int>(
                """SELECT DISTINCT "TargetType" AS "Value" FROM bindings""")
            .ToListAsync();
        rawOrdinals.Should().ContainSingle().Which.Should().Be(
            (int)BindingTargetType.Agent,
            "SD4.2 added Agent = 6; existing rows on disk depend on the stable ordinal");
    }

    [SkippableFact]
    public async Task Postgres_HighRiskRulesJson_RoundTripsApproverChain()
    {
        // PolicyVersion.RulesJson is jsonb on Postgres. The high-risk
        // approver chain is the load-bearing payload — Conductor's
        // ActionBus parses this to render the typed-confirmation prompt.
        Skip.IfNot(_dockerAvailable);
        await using var db = await NewPostgresAsync();

        await PolicySeeder.SeedStockPoliciesAsync(db);

        var highRisk = await db.Policies.AsNoTracking()
            .SingleAsync(p => p.Name == "high-risk");
        var version = await db.PolicyVersions.AsNoTracking()
            .SingleAsync(v => v.PolicyId == highRisk.Id);

        using var doc = JsonDocument.Parse(version.RulesJson);
        doc.RootElement.GetProperty("requireTypedConfirmation").GetBoolean().Should().BeTrue();
        var approvers = doc.RootElement.GetProperty("approvers");
        approvers.GetArrayLength().Should().Be(1);
        approvers[0].GetProperty("role").GetString().Should().Be("maintainer");
        approvers[0].GetProperty("minApprovals").GetInt32().Should().Be(1);
        approvers[0].GetProperty("selfApprovalForbidden").GetBoolean().Should().BeTrue();
    }

    [SkippableFact]
    public async Task Postgres_Reseed_IsNoOp_NoDuplicates()
    {
        Skip.IfNot(_dockerAvailable);
        await using var db = await NewPostgresAsync();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);
        var firstBundleCount = await db.Bundles.CountAsync();

        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);
        await PolicySeeder.SeedStockPoliciesAsync(db);
        await BindingSeeder.SeedDefaultBindingsAsync(db);

        (await db.Policies.CountAsync()).Should().Be(6);
        (await db.PolicyVersions.CountAsync()).Should().Be(6);
        (await db.Bindings.CountAsync(b => b.TargetType == BindingTargetType.Agent))
            .Should().Be(19);
        (await db.Bundles.CountAsync()).Should().Be(
            firstBundleCount,
            "SD4 contract: reseed never bumps the bundle snapshot");
    }

    [SkippableFact]
    public async Task Postgres_BindingTargetTypeColumn_IsStoredAsInteger()
    {
        // The migration declares `TargetType` as `integer` on Postgres;
        // SD4.2 added Agent = 6 but did not require a migration because
        // the column type is the same. Pin the schema so a drive-by
        // change to HasConversion<string>() (or similar) breaks loudly.
        Skip.IfNot(_dockerAvailable);
        await using var db = await NewPostgresAsync();

        var udtName = (await db.Database.SqlQueryRaw<string>(
                """
                SELECT udt_name AS "Value"
                FROM information_schema.columns
                WHERE table_name = 'bindings' AND column_name = 'TargetType'
                """).ToListAsync()).Single();

        udtName.Should().Be(
            "int4",
            "Binding.TargetType is HasConversion<int>; SD4.2 Agent = 6 round-trips " +
            "as an int4 ordinal on Postgres. A flip to string storage would break " +
            "existing rows on disk.");
    }
}
