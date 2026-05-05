// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Events;
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

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// P5.2 (#52) — exercises the propose/approve/revoke flows of
/// <see cref="OverrideService"/> over EF Core InMemory. The serializable
/// transaction semantics (concurrent racers, optimistic concurrency
/// rejection) require a real provider and live in the integration suite.
/// </summary>
public class OverrideServiceTests
{
    private const string Proposer = "user:proposer";
    private const string Approver = "user:approver";

    private static (
        OverrideService service,
        AppDbContext db,
        RecordingDispatcher events,
        StubRbac rbac,
        FakeTimeProvider clock)
        NewService(bool rbacAllow = true)
    {
        var db = InMemoryDbFixture.Create();
        var events = new RecordingDispatcher();
        var rbac = new StubRbac { Allow = rbacAllow };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero));
        var service = new OverrideService(db, rbac, events, clock);
        return (service, db, events, rbac, clock);
    }

    private static async Task<(Policy policy, PolicyVersion active)> SeedActiveAsync(
        AppDbContext db, string name)
    {
        var policy = new Policy { Id = Guid.NewGuid(), Name = name, CreatedBySubjectId = "u1" };
        var version = PolicyBuilders.AVersion(policy.Id, number: 1, state: LifecycleState.Active);
        version.Policy = policy;
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return (policy, version);
    }

    private static ProposeOverrideRequest ExemptRequest(
        Guid policyVersionId,
        DateTimeOffset expiresAt,
        string scopeRef = "user:42",
        string rationale = "expedite review for vendor-blocked story") =>
        new(
            PolicyVersionId: policyVersionId,
            ScopeKind: OverrideScopeKind.Principal,
            ScopeRef: scopeRef,
            Effect: OverrideEffect.Exempt,
            ReplacementPolicyVersionId: null,
            ExpiresAt: expiresAt,
            Rationale: rationale);

    // ----- ProposeAsync ------------------------------------------------

    [Fact]
    public async Task ProposeAsync_HappyPath_PersistsAndDispatchesEvent()
    {
        var (svc, db, events, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p1");

        var dto = await svc.ProposeAsync(
            ExemptRequest(version.Id, expiresAt: clock.GetUtcNow().AddDays(1)),
            Proposer);

        dto.State.Should().Be(OverrideState.Proposed);
        dto.PolicyVersionId.Should().Be(version.Id);
        dto.ProposerSubjectId.Should().Be(Proposer);
        dto.ApproverSubjectId.Should().BeNull();
        dto.ApprovedAt.Should().BeNull();

        var row = await db.Overrides.AsNoTracking().FirstAsync(o => o.Id == dto.Id);
        row.State.Should().Be(OverrideState.Proposed);
        row.ProposedAt.Should().Be(clock.GetUtcNow());

        events.Events.Should().ContainSingle(e => e is OverrideProposed);
    }

    [Fact]
    public async Task ProposeAsync_BlankScopeRef_ThrowsValidation()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p2");
        var bad = ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)) with { ScopeRef = "   " };

        var act = async () => await svc.ProposeAsync(bad, Proposer);

        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*ScopeRef is required*");
    }

    [Fact]
    public async Task ProposeAsync_BlankRationale_ThrowsValidation()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p3");
        var bad = ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)) with { Rationale = "" };

        await FluentActions.Invoking(() => svc.ProposeAsync(bad, Proposer))
            .Should().ThrowAsync<ValidationException>()
            .WithMessage("*Rationale is required*");
    }

    [Fact]
    public async Task ProposeAsync_ScopeRefTooLong_ThrowsValidation()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p3-len");
        var bad = ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)) with
        {
            ScopeRef = new string('x', 257),
        };

        await FluentActions.Invoking(() => svc.ProposeAsync(bad, Proposer))
            .Should().ThrowAsync<ValidationException>()
            .WithMessage("*256-char limit*");
    }

    [Fact]
    public async Task ProposeAsync_ReplaceWithNullReplacement_ThrowsValidation()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p4");
        var bad = new ProposeOverrideRequest(
            PolicyVersionId: version.Id,
            ScopeKind: OverrideScopeKind.Principal,
            ScopeRef: "user:1",
            Effect: OverrideEffect.Replace,
            ReplacementPolicyVersionId: null,
            ExpiresAt: clock.GetUtcNow().AddDays(1),
            Rationale: "swap policy");

        await FluentActions.Invoking(() => svc.ProposeAsync(bad, Proposer))
            .Should().ThrowAsync<ValidationException>()
            .WithMessage("*Effect=Replace requires*");
    }

    [Fact]
    public async Task ProposeAsync_ExemptWithReplacement_ThrowsValidation()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p5");
        var bad = new ProposeOverrideRequest(
            PolicyVersionId: version.Id,
            ScopeKind: OverrideScopeKind.Cohort,
            ScopeRef: "cohort:beta",
            Effect: OverrideEffect.Exempt,
            ReplacementPolicyVersionId: Guid.NewGuid(),
            ExpiresAt: clock.GetUtcNow().AddDays(1),
            Rationale: "exempt");

        await FluentActions.Invoking(() => svc.ProposeAsync(bad, Proposer))
            .Should().ThrowAsync<ValidationException>()
            .WithMessage("*Effect=Exempt requires a null*");
    }

    [Fact]
    public async Task ProposeAsync_ExpiresInThePast_ThrowsValidation()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p6");
        var bad = ExemptRequest(version.Id, clock.GetUtcNow().AddSeconds(30));

        await FluentActions.Invoking(() => svc.ProposeAsync(bad, Proposer))
            .Should().ThrowAsync<ValidationException>()
            .WithMessage("*minute*");
    }

    [Fact]
    public async Task ProposeAsync_UnknownPolicyVersion_ThrowsNotFound()
    {
        var (svc, _, _, _, clock) = NewService();
        var bad = ExemptRequest(Guid.NewGuid(), clock.GetUtcNow().AddDays(1));

        await FluentActions.Invoking(() => svc.ProposeAsync(bad, Proposer))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ProposeAsync_RetiredPolicyVersion_ThrowsValidation()
    {
        var (svc, db, _, _, clock) = NewService();
        var policy = new Policy { Id = Guid.NewGuid(), Name = "retired", CreatedBySubjectId = "u" };
        var retired = PolicyBuilders.AVersion(policy.Id, number: 1, state: LifecycleState.Retired);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(retired);
        await db.SaveChangesAsync();

        var bad = ExemptRequest(retired.Id, clock.GetUtcNow().AddDays(1));

        await FluentActions.Invoking(() => svc.ProposeAsync(bad, Proposer))
            .Should().ThrowAsync<ValidationException>()
            .WithMessage("*retired*");
    }

    [Fact]
    public async Task ProposeAsync_RetiredReplacement_ThrowsValidation()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, primary) = await SeedActiveAsync(db, "primary");
        var retired = PolicyBuilders.AVersion(primary.PolicyId, number: 2, state: LifecycleState.Retired);
        db.PolicyVersions.Add(retired);
        await db.SaveChangesAsync();

        var bad = new ProposeOverrideRequest(
            PolicyVersionId: primary.Id,
            ScopeKind: OverrideScopeKind.Principal,
            ScopeRef: "user:1",
            Effect: OverrideEffect.Replace,
            ReplacementPolicyVersionId: retired.Id,
            ExpiresAt: clock.GetUtcNow().AddDays(1),
            Rationale: "swap");

        await FluentActions.Invoking(() => svc.ProposeAsync(bad, Proposer))
            .Should().ThrowAsync<ValidationException>()
            .WithMessage("*retired replacement*");
    }

    // ----- ApproveAsync ------------------------------------------------

    [Fact]
    public async Task ApproveAsync_HappyPath_TransitionsAndDispatchesEvent()
    {
        var (svc, db, events, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p7");
        var proposed = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)),
            Proposer);

        clock.Advance(TimeSpan.FromMinutes(5));
        var approved = await svc.ApproveAsync(proposed.Id, Approver);

        approved.State.Should().Be(OverrideState.Approved);
        approved.ApproverSubjectId.Should().Be(Approver);
        approved.ApprovedAt.Should().Be(clock.GetUtcNow());

        events.Events.OfType<OverrideApproved>().Should().ContainSingle();
    }

    [Fact]
    public async Task ApproveAsync_SelfApproval_ThrowsBeforeRbacCheck()
    {
        var (svc, db, events, rbac, clock) = NewService(rbacAllow: true);
        var (_, version) = await SeedActiveAsync(db, "p8");
        var proposed = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)),
            Proposer);

        await FluentActions.Invoking(() => svc.ApproveAsync(proposed.Id, Proposer))
            .Should().ThrowAsync<SelfApprovalException>();

        // Self-approval rejection happens *before* the RBAC check; the
        // stub records every call so we can assert it was never invoked.
        rbac.Calls.Should().BeEmpty();
        events.Events.OfType<OverrideApproved>().Should().BeEmpty();
    }

    [Fact]
    public async Task ApproveAsync_RbacDenied_ThrowsAndLeavesProposed()
    {
        var (svc, db, events, _, clock) = NewService(rbacAllow: false);
        var (_, version) = await SeedActiveAsync(db, "p9");
        var proposed = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)),
            Proposer);

        await FluentActions.Invoking(() => svc.ApproveAsync(proposed.Id, Approver))
            .Should().ThrowAsync<RbacDeniedException>();

        var row = await db.Overrides.AsNoTracking().FirstAsync(o => o.Id == proposed.Id);
        row.State.Should().Be(OverrideState.Proposed);
        events.Events.OfType<OverrideApproved>().Should().BeEmpty();
    }

    [Fact]
    public async Task ApproveAsync_AlreadyApproved_ThrowsConflict()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p10");
        var proposed = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)),
            Proposer);
        await svc.ApproveAsync(proposed.Id, Approver);

        await FluentActions.Invoking(() => svc.ApproveAsync(proposed.Id, "user:third"))
            .Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task ApproveAsync_UnknownId_ThrowsNotFound()
    {
        var (svc, _, _, _, _) = NewService();

        await FluentActions.Invoking(() => svc.ApproveAsync(Guid.NewGuid(), Approver))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ApproveAsync_PassesScopeRefAsResourceInstance()
    {
        var (svc, db, _, rbac, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p10b");
        var proposed = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1), scopeRef: "cohort:beta"),
            Proposer);

        await svc.ApproveAsync(proposed.Id, Approver);

        rbac.Calls.Should().ContainSingle(c =>
            c.Permission == "andy-policies:override:approve" &&
            c.ResourceInstanceId == "cohort:beta" &&
            c.SubjectId == Approver);
    }

    // ----- RevokeAsync -------------------------------------------------

    [Fact]
    public async Task RevokeAsync_FromProposed_TransitionsAndDispatches()
    {
        var (svc, db, events, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p11");
        var proposed = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)),
            Proposer);

        var revoked = await svc.RevokeAsync(
            proposed.Id, new RevokeOverrideRequest("withdrawn"), Approver);

        revoked.State.Should().Be(OverrideState.Revoked);
        revoked.RevocationReason.Should().Be("withdrawn");
        events.Events.OfType<OverrideRevoked>().Should().ContainSingle();
    }

    [Fact]
    public async Task RevokeAsync_FromApproved_TransitionsAndDispatches()
    {
        var (svc, db, events, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p12");
        var proposed = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)),
            Proposer);
        await svc.ApproveAsync(proposed.Id, Approver);

        var revoked = await svc.RevokeAsync(
            proposed.Id, new RevokeOverrideRequest("regression detected"), Approver);

        revoked.State.Should().Be(OverrideState.Revoked);
        events.Events.OfType<OverrideRevoked>().Should().ContainSingle();
    }

    [Fact]
    public async Task RevokeAsync_BlankReason_ThrowsValidation()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p13");
        var proposed = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)),
            Proposer);

        await FluentActions.Invoking(() => svc.RevokeAsync(
                proposed.Id, new RevokeOverrideRequest("   "), Approver))
            .Should().ThrowAsync<ValidationException>()
            .WithMessage("*RevocationReason is required*");
    }

    [Fact]
    public async Task RevokeAsync_AlreadyRevoked_ThrowsConflict()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p14");
        var proposed = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)),
            Proposer);
        await svc.RevokeAsync(proposed.Id, new RevokeOverrideRequest("oops"), Approver);

        await FluentActions.Invoking(() => svc.RevokeAsync(
                proposed.Id, new RevokeOverrideRequest("again"), Approver))
            .Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task RevokeAsync_RbacDenied_LeavesStateUnchanged()
    {
        // Two-checker setup: propose+approve with allow=true, then a
        // separate service instance with allow=false drives the revoke.
        var db = InMemoryDbFixture.Create();
        var events = new RecordingDispatcher();
        var allowingRbac = new StubRbac { Allow = true };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero));

        var allowingSvc = new OverrideService(db, allowingRbac, events, clock);
        var (_, version) = await SeedActiveAsync(db, "p15");
        var proposed = await allowingSvc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1)),
            Proposer);
        await allowingSvc.ApproveAsync(proposed.Id, Approver);

        var denyingRbac = new StubRbac { Allow = false };
        var denyingSvc = new OverrideService(db, denyingRbac, events, clock);

        await FluentActions.Invoking(() => denyingSvc.RevokeAsync(
                proposed.Id, new RevokeOverrideRequest("attempt"), "user:not-allowed"))
            .Should().ThrowAsync<RbacDeniedException>();

        var row = await db.Overrides.AsNoTracking().FirstAsync(o => o.Id == proposed.Id);
        row.State.Should().Be(OverrideState.Approved);
        row.RevocationReason.Should().BeNull();
    }

    // ----- GetActiveAsync / ListAsync ---------------------------------

    [Fact]
    public async Task GetActiveAsync_FiltersByScopeAndApprovedNonExpired()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p16");

        // Approved + non-expired (should appear).
        var liveDto = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1), scopeRef: "user:42"),
            Proposer);
        await svc.ApproveAsync(liveDto.Id, Approver);

        // Approved but expired (should NOT appear — clock advance after approval).
        var expiringDto = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddMinutes(10), scopeRef: "user:42"),
            Proposer);
        await svc.ApproveAsync(expiringDto.Id, Approver);
        clock.Advance(TimeSpan.FromMinutes(15));

        // Different scope (should NOT appear).
        var otherScopeDto = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1), scopeRef: "user:99"),
            Proposer);
        await svc.ApproveAsync(otherScopeDto.Id, Approver);

        // Proposed-but-not-approved (should NOT appear).
        await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1), scopeRef: "user:42"),
            Proposer);

        var active = await svc.GetActiveAsync(OverrideScopeKind.Principal, "user:42");
        active.Should().ContainSingle().Which.Id.Should().Be(liveDto.Id);
    }

    [Fact]
    public async Task ListAsync_WithStateFilter_ReturnsMatchingRows()
    {
        var (svc, db, _, _, clock) = NewService();
        var (_, version) = await SeedActiveAsync(db, "p17");

        var a = await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1), scopeRef: "user:1"),
            Proposer);
        await svc.ProposeAsync(
            ExemptRequest(version.Id, clock.GetUtcNow().AddDays(1), scopeRef: "user:2"),
            Proposer);
        await svc.ApproveAsync(a.Id, Approver);

        var proposedOnly = await svc.ListAsync(new OverrideListFilter(State: OverrideState.Proposed));
        proposedOnly.Should().HaveCount(1);
        proposedOnly[0].State.Should().Be(OverrideState.Proposed);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        var (svc, _, _, _, _) = NewService();

        var result = await svc.GetAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    // ----- Test doubles ------------------------------------------------

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

    /// <summary>
    /// Minimal fake <see cref="TimeProvider"/> for unit tests — avoids
    /// taking a dependency on <c>Microsoft.Extensions.TimeProvider.Testing</c>.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class StubRbac : IRbacChecker
    {
        public bool Allow { get; set; } = true;

        public List<(string SubjectId, string Permission, string? ResourceInstanceId)> Calls { get; } = new();

        public Task<RbacDecision> CheckAsync(
            string subjectId, string permissionCode, IReadOnlyList<string> groups,
            string? resourceInstanceId, CancellationToken ct)
        {
            Calls.Add((subjectId, permissionCode, resourceInstanceId));
            return Task.FromResult(Allow
                ? RbacDecision.Allow("stub allowed")
                : RbacDecision.Deny("stub denied"));
        }
    }
}
