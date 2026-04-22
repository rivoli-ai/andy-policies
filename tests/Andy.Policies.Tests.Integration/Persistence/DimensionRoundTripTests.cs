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
/// Round-trip tests for the three P1.2 dimensions on <see cref="PolicyVersion"/>.
/// SQLite-backed only in this PR — the Postgres <c>text[]</c> path is verified
/// by the Testcontainers suite landing in P1.11 (rivoli-ai/andy-policies#91).
/// </summary>
public class DimensionRoundTripTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public DimensionRoundTripTests()
    {
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

    private async Task<AppDbContext> CreateMigratedDbAsync()
    {
        var db = new AppDbContext(_options);
        await db.Database.MigrateAsync();
        return db;
    }

    [Theory]
    [InlineData(EnforcementLevel.May)]
    [InlineData(EnforcementLevel.Should)]
    [InlineData(EnforcementLevel.Must)]
    public async Task SaveAndReload_PreservesEnforcement(EnforcementLevel level)
    {
        using var db = await CreateMigratedDbAsync();
        var policy = new Policy { Id = Guid.NewGuid(), Name = $"p-{level.ToString().ToLowerInvariant()}", CreatedBySubjectId = "u1" };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = LifecycleState.Draft,
            Enforcement = level,
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();

        var loaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == version.Id);
        Assert.Equal(level, loaded.Enforcement);
    }

    [Theory]
    [InlineData(Severity.Info)]
    [InlineData(Severity.Moderate)]
    [InlineData(Severity.Critical)]
    public async Task SaveAndReload_PreservesSeverity(Severity severity)
    {
        using var db = await CreateMigratedDbAsync();
        var policy = new Policy { Id = Guid.NewGuid(), Name = $"s-{severity.ToString().ToLowerInvariant()}", CreatedBySubjectId = "u1" };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = LifecycleState.Draft,
            Severity = severity,
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();

        var loaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == version.Id);
        Assert.Equal(severity, loaded.Severity);
    }

    [Fact]
    public async Task SaveAndReload_PreservesScopes_OnSqlite()
    {
        // AC scenario: round-trip the exact payload called out in the P1.2 story.
        using var db = await CreateMigratedDbAsync();
        var policy = new Policy { Id = Guid.NewGuid(), Name = "no-prod", CreatedBySubjectId = "u1" };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = LifecycleState.Draft,
            Enforcement = EnforcementLevel.Must,
            Severity = Severity.Critical,
            Scopes = new List<string> { "prod", "tool:write" },
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();

        var loaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == version.Id);
        Assert.Equal(EnforcementLevel.Must, loaded.Enforcement);
        Assert.Equal(Severity.Critical, loaded.Severity);
        Assert.Equal(new[] { "prod", "tool:write" }, loaded.Scopes);
    }

    [Fact]
    public async Task EmptyScopes_RoundTripCleanly()
    {
        using var db = await CreateMigratedDbAsync();
        var policy = new Policy { Id = Guid.NewGuid(), Name = "universal", CreatedBySubjectId = "u1" };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = LifecycleState.Draft,
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();

        var loaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == version.Id);
        Assert.NotNull(loaded.Scopes);
        Assert.Empty(loaded.Scopes);
    }

    [Fact]
    public async Task EnforcementAndSeverity_StoredAsStrings()
    {
        // Guarantees the wire-format contract (ADR 0001 §6): Enforcement/Severity are
        // persisted as string tokens, not integer ordinals. This is load-bearing for
        // cross-service consumers and for the P6 audit chain (canonical JSON over a
        // PolicyVersion must serialise the same tokens that the DB holds).
        using var db = await CreateMigratedDbAsync();
        var policy = new Policy { Id = Guid.NewGuid(), Name = "string-shape", CreatedBySubjectId = "u1" };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = LifecycleState.Draft,
            Enforcement = EnforcementLevel.Must,
            Severity = Severity.Critical,
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Enforcement, Severity FROM policy_versions LIMIT 1";

        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Must", reader.GetString(0));
        Assert.Equal("Critical", reader.GetString(1));
    }
}
