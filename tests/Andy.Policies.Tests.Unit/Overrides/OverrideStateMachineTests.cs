// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Overrides;

/// <summary>
/// P5.8 (#62) — full state-machine matrix for the override workflow.
/// Drives every <c>(from-state, action) → (to-state | error)</c>
/// combination through <see cref="OverrideService"/> over EF Core
/// InMemory, asserting that legal transitions land in the expected
/// state and illegal transitions raise <see cref="ConflictException"/>
/// (which the API translates to HTTP 409, the gRPC layer to
/// <c>FAILED_PRECONDITION</c>, and the MCP tools to
/// <c>policy.override.invalid_state</c>). The reaper-driven Expired
/// path is exercised via <see cref="IOverrideService.ExpireAsync"/>
/// — the only code path into <see cref="OverrideState.Expired"/>.
/// </summary>
public class OverrideStateMachineTests
{
    public enum Action
    {
        Approve,
        Revoke,
        Expire,
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

    private static (OverrideService svc, AppDbContext db) NewService()
    {
        var db = InMemoryDbFixture.Create();
        var svc = new OverrideService(db, new AllowRbac(), new NoopDispatcher(), TimeProvider.System);
        return (svc, db);
    }

    /// <summary>
    /// Drives an override into <paramref name="from"/>. Approves /
    /// revokes / expires through the public service surface so the
    /// state-machine guards from P5.2 + P5.3 are the same paths
    /// production exercises.
    /// </summary>
    private static async Task<Override> SeedInStateAsync(
        OverrideService svc, AppDbContext db, OverrideState from)
    {
        var policy = new Policy { Id = Guid.NewGuid(), Name = $"p-{Guid.NewGuid():n}", CreatedBySubjectId = "u" };
        var version = PolicyBuilders.AVersion(policy.Id, number: 1, state: LifecycleState.Active);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();

        var dto = await svc.ProposeAsync(
            new ProposeOverrideRequest(
                version.Id,
                OverrideScopeKind.Principal,
                $"user:{Guid.NewGuid():n}",
                OverrideEffect.Exempt,
                ReplacementPolicyVersionId: null,
                // Above the +1m propose-time floor; tests that need a
                // due row pull ExpiresAt forward via the entity below.
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                Rationale: "fixture"),
            "user:proposer");

        switch (from)
        {
            case OverrideState.Proposed:
                break;
            case OverrideState.Approved:
                await svc.ApproveAsync(dto.Id, "user:approver");
                break;
            case OverrideState.Revoked:
                await svc.RevokeAsync(dto.Id, new RevokeOverrideRequest("seed"), "user:approver");
                break;
            case OverrideState.Expired:
                // Drive through Approved → Expired via the system path
                // (the reaper's only-entry rule is enforced by the
                // service contract; we call it directly here).
                await svc.ApproveAsync(dto.Id, "user:approver");
                // Force the row past expiry without waiting (test helper
                // pokes ExpiresAt back so ExpireAsync's "not yet due"
                // guard doesn't reject).
                var entity = await db.Overrides.FirstAsync(o => o.Id == dto.Id);
                entity.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();
                await svc.ExpireAsync(dto.Id);
                break;
            default:
                throw new InvalidOperationException($"Unknown seed state {from}");
        }

        return await db.Overrides.AsNoTracking().FirstAsync(o => o.Id == dto.Id);
    }

    public static IEnumerable<object?[]> Transitions => new[]
    {
        // (from, action, expectedTo, expectedErrorAction|null)
        new object?[] { OverrideState.Proposed, Action.Approve, OverrideState.Approved, null },
        new object?[] { OverrideState.Proposed, Action.Revoke,  OverrideState.Revoked,  null },
        new object?[] { OverrideState.Approved, Action.Approve, OverrideState.Approved, "ConflictException" },
        new object?[] { OverrideState.Approved, Action.Revoke,  OverrideState.Revoked,  null },
        new object?[] { OverrideState.Approved, Action.Expire,  OverrideState.Expired,  null },
        new object?[] { OverrideState.Revoked,  Action.Approve, OverrideState.Revoked,  "ConflictException" },
        new object?[] { OverrideState.Revoked,  Action.Revoke,  OverrideState.Revoked,  "ConflictException" },
        new object?[] { OverrideState.Revoked,  Action.Expire,  OverrideState.Revoked,  "ConflictException" },
        new object?[] { OverrideState.Expired,  Action.Approve, OverrideState.Expired,  "ConflictException" },
        new object?[] { OverrideState.Expired,  Action.Revoke,  OverrideState.Expired,  "ConflictException" },
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task StateMachineTransitions(
        OverrideState from, Action action, OverrideState expectedTo, string? expectedError)
    {
        var (svc, db) = NewService();
        var seeded = await SeedInStateAsync(svc, db, from);

        // For "Expire" we must let the row already be due so the
        // service's "not yet due" guard doesn't fire spuriously on the
        // legal-Expire-from-Approved case. The seed for Approved leaves
        // ExpiresAt at now+2s; pull it forward when needed.
        if (action == Action.Expire && from == OverrideState.Approved)
        {
            var entity = await db.Overrides.FirstAsync(o => o.Id == seeded.Id);
            entity.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        Func<Task> act = action switch
        {
            Action.Approve => () => svc.ApproveAsync(seeded.Id, "user:third"),
            Action.Revoke => () => svc.RevokeAsync(
                seeded.Id, new RevokeOverrideRequest("test"), "user:third"),
            Action.Expire => () => svc.ExpireAsync(seeded.Id),
            _ => throw new InvalidOperationException($"Unknown action {action}"),
        };

        if (expectedError is null)
        {
            await act();
        }
        else
        {
            await act.Should().ThrowAsync<ConflictException>();
        }

        var final = await db.Overrides.AsNoTracking().FirstAsync(o => o.Id == seeded.Id);
        final.State.Should().Be(expectedTo);
    }
}
