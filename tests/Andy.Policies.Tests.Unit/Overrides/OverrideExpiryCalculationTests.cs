// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Overrides;

/// <summary>
/// P5.8 (#62) — boundary cases for the propose-time expiry floor
/// (P5.2 enforces ExpiresAt > now + 1 minute). The reaper picks up
/// "current time vs ExpiresAt" comparisons separately (P5.3); this
/// suite isolates the propose-time validation contract.
/// </summary>
public class OverrideExpiryCalculationTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class AllowRbac : IRbacChecker
    {
        public Task<RbacCheckResult> CheckAsync(
            string subjectId, string permission, string? resourceInstanceId, CancellationToken ct = default)
            => Task.FromResult(RbacCheckResult.AllowedResult);
    }

    private sealed class NoopDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
            where TEvent : notnull => Task.CompletedTask;
    }

    private static (OverrideService svc, AppDbContext db, FakeTimeProvider clock) NewService(DateTimeOffset now)
    {
        var db = InMemoryDbFixture.Create();
        var clock = new FakeTimeProvider(now);
        var svc = new OverrideService(db, new AllowRbac(), new NoopDispatcher(), clock);
        return (svc, db, clock);
    }

    private static async Task<Guid> SeedActiveVersionAsync(AppDbContext db)
    {
        var policy = new Andy.Policies.Domain.Entities.Policy
        {
            Id = Guid.NewGuid(),
            Name = $"p-{Guid.NewGuid():n}",
            CreatedBySubjectId = "u",
        };
        var version = PolicyBuilders.AVersion(policy.Id, number: 1, state: LifecycleState.Active);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return version.Id;
    }

    private static ProposeOverrideRequest Request(Guid pvid, DateTimeOffset expiresAt) => new(
        pvid,
        OverrideScopeKind.Principal,
        "user:42",
        OverrideEffect.Exempt,
        ReplacementPolicyVersionId: null,
        ExpiresAt: expiresAt,
        Rationale: "fixture");

    [Fact]
    public async Task ProposeAsync_ExpiresAt1SecondFuture_RejectsBelowOneMinuteFloor()
    {
        var now = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var (svc, db, _) = NewService(now);
        var pvid = await SeedActiveVersionAsync(db);

        var act = () => svc.ProposeAsync(Request(pvid, now.AddSeconds(1)), "user:proposer");

        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*minute*");
    }

    [Fact]
    public async Task ProposeAsync_ExpiresAt2MinutesFuture_AcceptsAboveOneMinuteFloor()
    {
        var now = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var (svc, db, _) = NewService(now);
        var pvid = await SeedActiveVersionAsync(db);

        var dto = await svc.ProposeAsync(Request(pvid, now.AddMinutes(2)), "user:proposer");

        dto.State.Should().Be(OverrideState.Proposed);
        dto.ExpiresAt.Should().Be(now.AddMinutes(2));
    }

    [Fact]
    public async Task ProposeAsync_ExpiresAtInPast_RejectsWithValidation()
    {
        var now = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var (svc, db, _) = NewService(now);
        var pvid = await SeedActiveVersionAsync(db);

        var act = () => svc.ProposeAsync(Request(pvid, now.AddDays(-1)), "user:proposer");

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ProposeAsync_ExpiresAtAcrossDstBoundary_StoredAsUtc()
    {
        // DateTimeOffset is timezone-agnostic on the wire — we always
        // serialize to UTC ("o" format). This case verifies that a
        // DST-boundary value (e.g. spring-forward) round-trips
        // unchanged through the service layer. The clock is fixed
        // before the boundary; ExpiresAt is set 8 hours after, which
        // crosses 2026-03-08 02:00 → 03:00 in US Eastern.
        var nowEt = new DateTimeOffset(2026, 3, 8, 1, 0, 0, TimeSpan.FromHours(-5));
        var nowUtc = nowEt.ToUniversalTime();
        var (svc, db, _) = NewService(nowUtc);
        var pvid = await SeedActiveVersionAsync(db);

        var expiresEt = new DateTimeOffset(2026, 3, 8, 9, 0, 0, TimeSpan.FromHours(-4)); // post-DST
        var dto = await svc.ProposeAsync(Request(pvid, expiresEt), "user:proposer");

        // Service stores DateTimeOffset; the underlying instant is
        // preserved regardless of the input offset.
        dto.ExpiresAt.UtcDateTime.Should().Be(expiresEt.UtcDateTime);
    }
}
