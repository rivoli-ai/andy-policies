// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Persistence;

/// <summary>
/// Verifies the <c>SaveChangesAsync</c> immutability guard on <see cref="AppDbContext"/>.
/// EF InMemory is used so the test is fast and provider-agnostic — the guard itself is in
/// C# and runs before any SQL. Provider-specific migration tests live in the integration
/// suite (see <c>PolicyMigrationTests</c> in P1.11).
/// </summary>
public class AppDbContextImmutabilityGuardTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenMutatingDraftVersion_Succeeds()
    {
        using var db = CreateInMemoryDb();
        var policy = new Policy { Id = Guid.NewGuid(), Name = "write-branch", CreatedBySubjectId = "u1" };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = LifecycleState.Draft,
            Summary = "initial",
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();

        version.Summary = "edited";
        version.RulesJson = "{\"allow\":[\"read\"]}";

        await db.SaveChangesAsync();

        var loaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == version.Id);
        Assert.Equal("edited", loaded.Summary);
    }

    [Theory]
    [InlineData(LifecycleState.Active)]
    [InlineData(LifecycleState.WindingDown)]
    [InlineData(LifecycleState.Retired)]
    public async Task SaveChangesAsync_WhenMutatingContentOnNonDraftVersion_Throws(LifecycleState state)
    {
        using var db = CreateInMemoryDb();
        var policy = new Policy { Id = Guid.NewGuid(), Name = "no-prod", CreatedBySubjectId = "u1" };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = state,
            Summary = "locked",
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();

        version.Summary = "should-fail";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains(state.ToString(), ex.Message);
        Assert.Contains("Summary", ex.Message);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenTransitioningLifecycleFields_Allowed()
    {
        // A version in the Active state should still permit P2's supersede flow writing to
        // State, PublishedAt, SupersededByVersionId, and Revision — the allow-listed set
        // enumerated in AppDbContext.EnforcePolicyVersionImmutability.
        using var db = CreateInMemoryDb();
        var policy = new Policy { Id = Guid.NewGuid(), Name = "high-risk", CreatedBySubjectId = "u1" };
        var active = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = LifecycleState.Active,
            Summary = "v1",
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedBySubjectId = "u2",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(active);
        await db.SaveChangesAsync();

        active.State = LifecycleState.WindingDown;
        active.SupersededByVersionId = Guid.NewGuid();

        await db.SaveChangesAsync();

        var loaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == active.Id);
        Assert.Equal(LifecycleState.WindingDown, loaded.State);
        Assert.NotNull(loaded.SupersededByVersionId);
    }

    [Fact]
    public async Task SaveChangesAsync_MixedChanges_RejectsContentWriteEvenWithLifecycleWrite()
    {
        // Belt-and-braces: a caller that tries to smuggle a content edit into an otherwise
        // legitimate lifecycle transition must still be rejected.
        using var db = CreateInMemoryDb();
        var policy = new Policy { Id = Guid.NewGuid(), Name = "sandboxed", CreatedBySubjectId = "u1" };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            Policy = policy,
            Version = 1,
            State = LifecycleState.Active,
            Summary = "v1",
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();

        version.State = LifecycleState.WindingDown;
        version.RulesJson = "{\"hacked\":true}";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("RulesJson", ex.Message);
    }

    [Fact]
    public async Task SaveChangesAsync_RevisionBumps_OnModification()
    {
        using var db = CreateInMemoryDb();
        var policy = new Policy { Id = Guid.NewGuid(), Name = "read-only", CreatedBySubjectId = "u1" };
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

        var revisionBefore = version.Revision;
        version.Summary = "bumped";
        await db.SaveChangesAsync();

        Assert.Equal(revisionBefore + 1, version.Revision);
    }
}
