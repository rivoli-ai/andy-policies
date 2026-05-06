// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Andy.Policies.Tests.Unit.Infrastructure;

/// <summary>
/// P10.1 (#31): pin the SQLite-portability invariant on
/// <see cref="AppDbContext.OnModelCreating"/>. When the context is
/// built with <c>UseSqlite</c>, no entity column may carry a
/// Postgres-only type literal (<c>jsonb</c>, <c>timestamptz</c>,
/// <c>uuid</c>, etc.) — embedded mode (Conductor's bundled
/// deployment) reads the same model and the same migrations against
/// SQLite, so a Postgres-only column type would fail at boot.
/// </summary>
/// <remarks>
/// The test mirrors what an embedded-mode boot would do at startup:
/// build the model with the SQLite provider and walk every entity's
/// columns. A failure here means an <c>AppDbContext</c> change
/// introduced an unguarded <c>HasColumnType("jsonb")</c> (or similar)
/// without an <c>isNpgsql</c> branch — the existing pattern in
/// <c>OnModelCreating</c> for jsonb / text[] columns is the
/// reference.
/// </remarks>
public class SqliteModelCompatibilityTests
{
    /// <summary>
    /// Postgres-only column type literals that must not appear when
    /// the model is built with SQLite. Excludes <c>uuid</c> on
    /// purpose — EF Core's SQLite provider transparently maps GUIDs
    /// to <c>BLOB</c>, but the model annotations may carry the
    /// literal as a hint without harm. The other three are hard
    /// failures at migration time.
    /// </summary>
    private static readonly string[] ForbiddenColumnTypes = new[]
    {
        "jsonb",
        "timestamptz",
        "timestamp with time zone",
        "text[]",
    };

    [Fact]
    public void NoEntityColumn_DeclaresPostgresOnlyType_WhenContextUsesSqlite()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        using var db = new AppDbContext(options);

        var offending = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                var columnType = prop.GetColumnType();
                if (string.IsNullOrEmpty(columnType)) continue;
                if (ForbiddenColumnTypes.Any(forbidden =>
                    columnType.Contains(forbidden, StringComparison.OrdinalIgnoreCase)))
                {
                    offending.Add($"{entity.Name}.{prop.Name} = '{columnType}'");
                }
            }
        }

        offending.Should().BeEmpty(
            "embedded mode (P10.1 #31) reads the same model against SQLite. A " +
            "Postgres-only column type without an isNpgsql() branch in " +
            "OnModelCreating would fail at boot for Conductor operators. " +
            "Use the existing isNpgsql conditional pattern (see PolicyVersion." +
            "RulesJson, AuditEvent.FieldDiffJson) to fork the type per provider.");
    }

    [Fact]
    public void EveryEntityType_IsBuildable_AgainstSqliteProvider()
    {
        // Sanity: a model that throws on Build means we'd never even
        // reach migration. Defensive against future entity changes
        // that misuse provider-specific configuration.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        using var db = new AppDbContext(options);

        var entityCount = db.Model.GetEntityTypes().Count();
        entityCount.Should().BeGreaterThan(0,
            "AppDbContext must register at least one entity under SQLite; " +
            "an empty model means OnModelCreating shorted out somewhere");
    }
}
