// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Events;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.BackgroundServices;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Unit.Fixtures;
using Andy.Settings.Client;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Policies.Tests.Unit.BackgroundServices;

/// <summary>
/// P5.3 (#53) — exercises the sweep semantics of
/// <see cref="OverrideExpiryReaper"/> over EF Core InMemory + the real
/// <see cref="OverrideService"/>. The hosted-service loop (cadence
/// timing, OperationCanceledException handling) is exercised via the
/// integration suite where a real <c>WebApplicationFactory</c> drives
/// it end-to-end.
/// </summary>
public class OverrideExpiryReaperTests
{
    private static (
        OverrideExpiryReaper reaper,
        AppDbContext db,
        FakeTimeProvider clock,
        StubSnapshot settings,
        RecordingDispatcher events)
        NewReaper()
    {
        var db = InMemoryDbFixture.Create();
        var events = new RecordingDispatcher();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero));
        var settings = new StubSnapshot();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IRbacChecker>(new AllowRbac());
        services.AddSingleton<IDomainEventDispatcher>(events);
        services.AddSingleton<TimeProvider>(clock);
        services.AddScoped<IOverrideService, OverrideService>();
        var sp = services.BuildServiceProvider();

        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        var reaper = new OverrideExpiryReaper(
            scopes, settings, clock, NullLogger<OverrideExpiryReaper>.Instance);
        return (reaper, db, clock, settings, events);
    }

    private static async Task<Override> SeedApprovedAsync(
        AppDbContext db, FakeTimeProvider clock, DateTimeOffset expiresAt, string scopeRef = "user:42")
    {
        // Seed a Policy + Active PolicyVersion so the FK is valid even on
        // InMemory (which doesn't enforce FKs but the entity navigations
        // expect a referenced row to exist for OverrideService validation).
        var policy = new Policy { Id = Guid.NewGuid(), Name = $"p-{Guid.NewGuid():n}", CreatedBySubjectId = "u" };
        var version = PolicyBuilders.AVersion(policy.Id, number: 1, state: LifecycleState.Active);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);

        var ovr = new Override
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = version.Id,
            ScopeKind = OverrideScopeKind.Principal,
            ScopeRef = scopeRef,
            Effect = OverrideEffect.Exempt,
            ProposerSubjectId = "user:proposer",
            ApproverSubjectId = "user:approver",
            State = OverrideState.Approved,
            ProposedAt = clock.GetUtcNow().AddMinutes(-30),
            ApprovedAt = clock.GetUtcNow().AddMinutes(-20),
            ExpiresAt = expiresAt,
            Rationale = "test fixture",
        };
        db.Overrides.Add(ovr);
        await db.SaveChangesAsync();
        return ovr;
    }

    [Fact]
    public async Task SweepOnceAsync_NothingDue_ReturnsZero()
    {
        var (reaper, db, clock, _, events) = NewReaper();
        await SeedApprovedAsync(db, clock, expiresAt: clock.GetUtcNow().AddDays(1));

        var count = await reaper.SweepOnceAsync(CancellationToken.None);

        count.Should().Be(0);
        events.Events.OfType<OverrideExpired>().Should().BeEmpty();
    }

    [Fact]
    public async Task SweepOnceAsync_OneDueRow_ExpiresAndDispatches()
    {
        var (reaper, db, clock, _, events) = NewReaper();
        var due = await SeedApprovedAsync(db, clock, expiresAt: clock.GetUtcNow().AddSeconds(-1));

        var count = await reaper.SweepOnceAsync(CancellationToken.None);

        count.Should().Be(1);
        var row = await db.Overrides.AsNoTracking().FirstAsync(o => o.Id == due.Id);
        row.State.Should().Be(OverrideState.Expired);
        events.Events.OfType<OverrideExpired>().Should().ContainSingle()
            .Which.OverrideId.Should().Be(due.Id);
    }

    [Fact]
    public async Task SweepOnceAsync_MixedRows_ExpiresOnlyDue()
    {
        var (reaper, db, clock, _, _) = NewReaper();
        var dueA = await SeedApprovedAsync(db, clock, expiresAt: clock.GetUtcNow().AddSeconds(-30));
        var dueB = await SeedApprovedAsync(db, clock, expiresAt: clock.GetUtcNow().AddSeconds(-1));
        var future = await SeedApprovedAsync(db, clock, expiresAt: clock.GetUtcNow().AddHours(1));

        var count = await reaper.SweepOnceAsync(CancellationToken.None);

        count.Should().Be(2);
        var states = await db.Overrides.AsNoTracking()
            .Where(o => new[] { dueA.Id, dueB.Id, future.Id }.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.State);
        states[dueA.Id].Should().Be(OverrideState.Expired);
        states[dueB.Id].Should().Be(OverrideState.Expired);
        states[future.Id].Should().Be(OverrideState.Approved);
    }

    [Fact]
    public async Task SweepOnceAsync_RowAlreadyRevoked_SkipsAndContinues()
    {
        var (reaper, db, clock, _, events) = NewReaper();
        var due = await SeedApprovedAsync(db, clock, expiresAt: clock.GetUtcNow().AddSeconds(-1));
        var alreadyRevoked = await SeedApprovedAsync(db, clock, expiresAt: clock.GetUtcNow().AddSeconds(-1));

        // Simulate the race: the reaper sees both rows in its scan, but
        // by the time it tries to expire `alreadyRevoked` another actor
        // has flipped it to Revoked. ExpireAsync raises ConflictException;
        // the reaper must continue with the sibling.
        var revokeRow = await db.Overrides.FirstAsync(o => o.Id == alreadyRevoked.Id);
        revokeRow.State = OverrideState.Revoked;
        revokeRow.RevocationReason = "raced by another actor";
        await db.SaveChangesAsync();

        var count = await reaper.SweepOnceAsync(CancellationToken.None);

        count.Should().Be(1);
        var dueRow = await db.Overrides.AsNoTracking().FirstAsync(o => o.Id == due.Id);
        dueRow.State.Should().Be(OverrideState.Expired);
        events.Events.OfType<OverrideExpired>().Should().ContainSingle()
            .Which.OverrideId.Should().Be(due.Id);
    }

    [Fact]
    public void CurrentCadenceSeconds_FromSettings_ClampsToMinimum()
    {
        var (reaper, _, _, settings, _) = NewReaper();

        settings.IntValue = null; // unset → default
        reaper.CurrentCadenceSeconds.Should().Be(OverrideExpiryReaper.DefaultCadenceSeconds);

        settings.IntValue = 120;
        reaper.CurrentCadenceSeconds.Should().Be(120);

        settings.IntValue = 0; // hot-loop attempt
        reaper.CurrentCadenceSeconds.Should().Be(OverrideExpiryReaper.MinCadenceSeconds);

        settings.IntValue = -10;
        reaper.CurrentCadenceSeconds.Should().Be(OverrideExpiryReaper.MinCadenceSeconds);
    }

    [Fact]
    public async Task SweepOnceAsync_RespectsMaxRowsPerSweepCap()
    {
        // Cap is 500; seed cap+5 due rows and assert we expire exactly
        // the cap on this pass (subsequent sweeps drain the rest). Keeps
        // individual transactions bounded even under a backlog.
        var (reaper, db, clock, _, _) = NewReaper();
        var cap = OverrideExpiryReaper.MaxRowsPerSweep;
        for (var i = 0; i < cap + 5; i++)
        {
            await SeedApprovedAsync(db, clock, expiresAt: clock.GetUtcNow().AddSeconds(-1 - i));
        }

        var count = await reaper.SweepOnceAsync(CancellationToken.None);

        count.Should().Be(cap);
        var remainingApproved = await db.Overrides.AsNoTracking()
            .CountAsync(o => o.State == OverrideState.Approved);
        remainingApproved.Should().Be(5);
    }

    // ----- Test doubles ------------------------------------------------

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class StubSnapshot : ISettingsSnapshot
    {
        public int? IntValue { get; set; }

        public int? GetInt(string key) =>
            key == OverrideExpiryReaper.CadenceSettingKey ? IntValue : null;

        public bool? GetBool(string key) => null;

        public string? GetString(string key) => null;

        public IReadOnlyCollection<string> Keys => Array.Empty<string>();

        public DateTimeOffset? LastRefreshedAt => null;
    }

    private sealed class RecordingDispatcher : IDomainEventDispatcher
    {
        public List<object> Events { get; } = new();

        public Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
            where TEvent : notnull
        {
            Events.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class AllowRbac : IRbacChecker
    {
        public Task<RbacCheckResult> CheckAsync(
            string subjectId, string permission, string? resourceInstanceId, CancellationToken ct = default)
            => Task.FromResult(RbacCheckResult.AllowedResult);
    }
}
