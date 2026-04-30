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
/// P5.1 (#49) integration tests for the <see cref="Override"/>
/// table: migration applies cleanly on Sqlite, the partial index
/// over Approved-only rows is present, the FK Restrict on both
/// <c>PolicyVersionId</c> + <c>ReplacementPolicyVersionId</c> rejects
/// inserts against unknown versions, and the
/// <c>ck_overrides_effect_replacement</c> CHECK constraint enforces
/// the Replace ↔ replacement-non-null contract.
/// </summary>
public class OverrideMigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;

    public OverrideMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"andy-policies-overmig-{Guid.NewGuid():N}.db");
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
            Name = $"override-{Guid.NewGuid():N}".Substring(0, 24),
            CreatedBySubjectId = "test",
        };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            Version = 1,
            State = LifecycleState.Active,
            Enforcement = EnforcementLevel.Should,
            Severity = Severity.Moderate,
            Scopes = new List<string>(),
            Summary = "fixture",
            RulesJson = "{}",
            CreatedBySubjectId = "test",
            ProposerSubjectId = "test",
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedBySubjectId = "test",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return (policy.Id, version.Id);
    }

    [Fact]
    public async Task Migration_CreatesOverridesTable_WithExpectedIndexes()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        var indexes = await db.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='overrides'")
            .ToListAsync();

        indexes.Should().Contain(new[]
        {
            "ix_overrides_scope_state",
            "ix_overrides_expiry_approved",
        });
    }

    [Fact]
    public async Task Insert_ExemptOverride_WithNoReplacement_RoundTripsAllFields()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();
        var (_, versionId) = await SeedPolicyAndDraftAsync(db);

        var ovr = new Override
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = versionId,
            ScopeKind = OverrideScopeKind.Principal,
            ScopeRef = "user:42",
            Effect = OverrideEffect.Exempt,
            ProposerSubjectId = "alice",
            Rationale = "experimentation",
            ProposedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            State = OverrideState.Proposed,
        };
        db.Overrides.Add(ovr);
        await db.SaveChangesAsync();

        var reloaded = await db.Overrides.AsNoTracking().FirstAsync(o => o.Id == ovr.Id);
        reloaded.PolicyVersionId.Should().Be(versionId);
        reloaded.ScopeKind.Should().Be(OverrideScopeKind.Principal);
        reloaded.ScopeRef.Should().Be("user:42");
        reloaded.Effect.Should().Be(OverrideEffect.Exempt);
        reloaded.ReplacementPolicyVersionId.Should().BeNull();
        reloaded.State.Should().Be(OverrideState.Proposed);
    }

    [Fact]
    public async Task Insert_ReplaceOverride_WithReplacement_RoundTripsAllFields()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();
        var (_, originalVersionId) = await SeedPolicyAndDraftAsync(db);
        var (_, replacementVersionId) = await SeedPolicyAndDraftAsync(db);

        var ovr = new Override
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = originalVersionId,
            ScopeKind = OverrideScopeKind.Cohort,
            ScopeRef = "cohort:beta",
            Effect = OverrideEffect.Replace,
            ReplacementPolicyVersionId = replacementVersionId,
            ProposerSubjectId = "alice",
            Rationale = "swap to canary",
            ProposedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            State = OverrideState.Proposed,
        };
        db.Overrides.Add(ovr);
        await db.SaveChangesAsync();

        var reloaded = await db.Overrides.AsNoTracking().FirstAsync(o => o.Id == ovr.Id);
        reloaded.Effect.Should().Be(OverrideEffect.Replace);
        reloaded.ReplacementPolicyVersionId.Should().Be(replacementVersionId);
    }

    [Fact]
    public async Task Insert_ReplaceOverride_WithoutReplacement_ViolatesCheckConstraint()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();
        var (_, versionId) = await SeedPolicyAndDraftAsync(db);

        db.Overrides.Add(new Override
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = versionId,
            ScopeKind = OverrideScopeKind.Principal,
            ScopeRef = "user:42",
            Effect = OverrideEffect.Replace,   // Replace requires non-null replacement
            ReplacementPolicyVersionId = null,
            ProposerSubjectId = "alice",
            Rationale = "bad",
            ProposedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            State = OverrideState.Proposed,
        });

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Insert_ExemptOverride_WithReplacement_ViolatesCheckConstraint()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();
        var (_, originalVersionId) = await SeedPolicyAndDraftAsync(db);
        var (_, replacementVersionId) = await SeedPolicyAndDraftAsync(db);

        db.Overrides.Add(new Override
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = originalVersionId,
            ScopeKind = OverrideScopeKind.Principal,
            ScopeRef = "user:42",
            Effect = OverrideEffect.Exempt,    // Exempt requires null replacement
            ReplacementPolicyVersionId = replacementVersionId,
            ProposerSubjectId = "alice",
            Rationale = "bad",
            ProposedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            State = OverrideState.Proposed,
        });

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Insert_OverrideWithUnknownPolicyVersionId_ThrowsDbUpdateException()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();

        db.Overrides.Add(new Override
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = Guid.NewGuid(),  // never seeded
            ScopeKind = OverrideScopeKind.Principal,
            ScopeRef = "user:42",
            Effect = OverrideEffect.Exempt,
            ProposerSubjectId = "alice",
            Rationale = "orphan",
            ProposedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            State = OverrideState.Proposed,
        });

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Insert_OverrideWithUnknownReplacementVersionId_ThrowsDbUpdateException()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();
        var (_, versionId) = await SeedPolicyAndDraftAsync(db);

        db.Overrides.Add(new Override
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = versionId,
            ScopeKind = OverrideScopeKind.Principal,
            ScopeRef = "user:42",
            Effect = OverrideEffect.Replace,
            ReplacementPolicyVersionId = Guid.NewGuid(),  // never seeded
            ProposerSubjectId = "alice",
            Rationale = "orphan-replacement",
            ProposedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            State = OverrideState.Proposed,
        });

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Update_BumpsRevisionToken_ForOptimisticConcurrency()
    {
        await using var db = new AppDbContext(NewOptions());
        await db.Database.MigrateAsync();
        var (_, versionId) = await SeedPolicyAndDraftAsync(db);

        var ovr = new Override
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = versionId,
            ScopeKind = OverrideScopeKind.Principal,
            ScopeRef = "user:revision",
            Effect = OverrideEffect.Exempt,
            ProposerSubjectId = "alice",
            Rationale = "bump",
            ProposedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            State = OverrideState.Proposed,
        };
        db.Overrides.Add(ovr);
        await db.SaveChangesAsync();

        ovr.Rationale = "bump 2";
        await db.SaveChangesAsync();

        ovr.Revision.Should().BeGreaterThan(0,
            "BumpRevisions wires Override into the same revision-token cycle as PolicyVersion");
    }
}
