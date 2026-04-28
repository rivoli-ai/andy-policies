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
/// P3.1 (#19) integration tests for the <see cref="Binding"/> table:
/// migration applies cleanly on Sqlite, the foreign key restricts inserts
/// against unknown <c>PolicyVersion</c> rows, and the three indexes
/// (<c>ix_bindings_target</c>, <c>ix_bindings_policy_version</c>,
/// <c>ix_bindings_deleted_at</c>) are physically present so target-side
/// lookups (P3.3, P3.4, P4) hit covering indexes.
/// </summary>
public class BindingMigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;

    public BindingMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"andy-policies-bindmig-{Guid.NewGuid():N}.db");
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

    private async Task<(Guid policyId, Guid versionId)> SeedPolicyAndDraftAsync(AppDbContext db)
    {
        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = $"binding-fixture-{Guid.NewGuid():N}".Substring(0, 24),
            CreatedBySubjectId = "test",
        };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            Version = 1,
            State = LifecycleState.Draft,
            Enforcement = EnforcementLevel.Should,
            Severity = Severity.Moderate,
            Scopes = new List<string>(),
            Summary = "fixture",
            RulesJson = "{}",
            CreatedBySubjectId = "test",
            ProposerSubjectId = "test",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return (policy.Id, version.Id);
    }

    [Fact]
    public async Task Migration_CreatesBindingsTable_WithExpectedIndexes()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        var indexes = await db.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='bindings'")
            .ToListAsync();

        indexes.Should().Contain(new[]
        {
            "ix_bindings_target",
            "ix_bindings_policy_version",
            "ix_bindings_deleted_at",
        });
    }

    [Fact]
    public async Task Insert_Binding_WithKnownPolicyVersion_RoundTripsAllFields()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();
        var (_, versionId) = await SeedPolicyAndDraftAsync(db);

        var binding = new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = versionId,
            TargetType = BindingTargetType.Repo,
            TargetRef = "repo:rivoli-ai/andy-policies",
            BindStrength = BindStrength.Mandatory,
            CreatedBySubjectId = "sam",
        };
        db.Bindings.Add(binding);
        await db.SaveChangesAsync();

        var reloaded = await db.Bindings.AsNoTracking().FirstAsync(b => b.Id == binding.Id);
        reloaded.PolicyVersionId.Should().Be(versionId);
        reloaded.TargetType.Should().Be(BindingTargetType.Repo);
        reloaded.TargetRef.Should().Be("repo:rivoli-ai/andy-policies");
        reloaded.BindStrength.Should().Be(BindStrength.Mandatory);
        reloaded.CreatedBySubjectId.Should().Be("sam");
        reloaded.DeletedAt.Should().BeNull();
        reloaded.DeletedBySubjectId.Should().BeNull();
    }

    [Fact]
    public async Task Insert_Binding_WithUnknownPolicyVersionId_ThrowsDbUpdateException()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        db.Bindings.Add(new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = Guid.NewGuid(),  // never seeded
            TargetType = BindingTargetType.Template,
            TargetRef = "template:00000000-0000-0000-0000-000000000000",
            CreatedBySubjectId = "test",
        });

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task SoftDelete_StampsDeletedAtAndDeletedBy_WithoutLosingRow()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();
        var (_, versionId) = await SeedPolicyAndDraftAsync(db);

        var binding = new Binding
        {
            PolicyVersionId = versionId,
            TargetType = BindingTargetType.Tenant,
            TargetRef = $"tenant:{Guid.NewGuid()}",
            CreatedBySubjectId = "sam",
        };
        db.Bindings.Add(binding);
        await db.SaveChangesAsync();

        // P3.2 service implementation will do this; here we just exercise
        // the column shape so future writes can rely on it.
        binding.DeletedAt = DateTimeOffset.UtcNow;
        binding.DeletedBySubjectId = "sam";
        await db.SaveChangesAsync();

        var reloaded = await db.Bindings.AsNoTracking().FirstAsync(b => b.Id == binding.Id);
        reloaded.DeletedAt.Should().NotBeNull();
        reloaded.DeletedBySubjectId.Should().Be("sam");
    }
}
