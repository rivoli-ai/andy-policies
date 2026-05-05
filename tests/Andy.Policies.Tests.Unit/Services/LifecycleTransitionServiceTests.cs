// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

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
/// P2.2 (#12) — exercises the publish/supersede/retire flows of
/// <see cref="LifecycleTransitionService"/> over EF Core InMemory. The
/// concurrent-publish path requires a real provider (Postgres) and lives
/// in the integration suite.
/// </summary>
public class LifecycleTransitionServiceTests
{
    private static (LifecycleTransitionService service, AppDbContext db, RecordingDispatcher events) NewService()
    {
        var db = InMemoryDbFixture.Create();
        var events = new RecordingDispatcher();
        var service = new LifecycleTransitionService(
            db,
            new RequireNonEmptyRationalePolicy(),
            events,
            TimeProvider.System);
        return (service, db, events);
    }

    private static async Task<(Policy policy, PolicyVersion version)> SeedDraftAsync(AppDbContext db, string name)
    {
        var policy = new Policy { Id = Guid.NewGuid(), Name = name, CreatedBySubjectId = "u1" };
        var version = PolicyBuilders.AVersion(policy.Id, number: 1, state: LifecycleState.Draft);
        version.Policy = policy;
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return (policy, version);
    }

    [Fact]
    public async Task TransitionAsync_PublishesDraft_StampsPublishedFields()
    {
        var (svc, db, _) = NewService();
        var (policy, draft) = await SeedDraftAsync(db, "publish-draft");

        var dto = await svc.TransitionAsync(
            policy.Id, draft.Id, LifecycleState.Active, "go-live", "actor-1");

        dto.State.Should().Be("Active");
        var reloaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == draft.Id);
        reloaded.State.Should().Be(LifecycleState.Active);
        reloaded.PublishedAt.Should().NotBeNull();
        reloaded.PublishedBySubjectId.Should().Be("actor-1");
    }

    [Fact]
    public async Task TransitionAsync_PublishingNewVersion_AutoSupersedesPreviousActive()
    {
        var (svc, db, _) = NewService();
        var (policy, v1) = await SeedDraftAsync(db, "supersede");
        await svc.TransitionAsync(policy.Id, v1.Id, LifecycleState.Active, "v1-live", "actor-1");

        var v2 = PolicyBuilders.AVersion(policy.Id, number: 2, state: LifecycleState.Draft);
        db.PolicyVersions.Add(v2);
        await db.SaveChangesAsync();

        await svc.TransitionAsync(policy.Id, v2.Id, LifecycleState.Active, "v2-live", "actor-2");

        var states = await db.PolicyVersions
            .AsNoTracking()
            .Where(v => v.PolicyId == policy.Id)
            .OrderBy(v => v.Version)
            .Select(v => new { v.Id, v.State, v.SupersededByVersionId })
            .ToListAsync();
        states.Should().SatisfyRespectively(
            v1State =>
            {
                v1State.State.Should().Be(LifecycleState.WindingDown);
                v1State.SupersededByVersionId.Should().Be(v2.Id);
            },
            v2State =>
            {
                v2State.State.Should().Be(LifecycleState.Active);
                v2State.SupersededByVersionId.Should().BeNull();
            });
    }

    [Fact]
    public async Task TransitionAsync_PublishWithSupersede_DispatchesSupersededBeforePublished()
    {
        var (svc, db, events) = NewService();
        var (policy, v1) = await SeedDraftAsync(db, "ordered-events");
        await svc.TransitionAsync(policy.Id, v1.Id, LifecycleState.Active, "v1", "actor-1");
        events.Events.Clear();

        var v2 = PolicyBuilders.AVersion(policy.Id, number: 2, state: LifecycleState.Draft);
        db.PolicyVersions.Add(v2);
        await db.SaveChangesAsync();

        await svc.TransitionAsync(policy.Id, v2.Id, LifecycleState.Active, "v2", "actor-2");

        events.Events.Should().HaveCount(2);
        events.Events[0].Should().BeOfType<PolicyVersionSuperseded>();
        events.Events[1].Should().BeOfType<PolicyVersionPublished>();
    }

    [Fact]
    public async Task TransitionAsync_RetireFromWindingDown_StampsRetiredAtAndEmitsEvent()
    {
        var (svc, db, events) = NewService();
        var (policy, v1) = await SeedDraftAsync(db, "retire-flow");
        await svc.TransitionAsync(policy.Id, v1.Id, LifecycleState.Active, "live", "actor-1");
        await svc.TransitionAsync(policy.Id, v1.Id, LifecycleState.WindingDown, "wind-down", "actor-1");
        events.Events.Clear();

        await svc.TransitionAsync(policy.Id, v1.Id, LifecycleState.Retired, "tomb", "actor-1");

        var reloaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == v1.Id);
        reloaded.State.Should().Be(LifecycleState.Retired);
        reloaded.RetiredAt.Should().NotBeNull();
        events.Events.Should().ContainSingle().Which.Should().BeOfType<PolicyVersionRetired>();
    }

    [Fact]
    public async Task TransitionAsync_FromRetired_ThrowsInvalidLifecycleTransition()
    {
        var (svc, db, _) = NewService();
        var (policy, v1) = await SeedDraftAsync(db, "tombstoned");
        await svc.TransitionAsync(policy.Id, v1.Id, LifecycleState.Active, "live", "actor-1");
        await svc.TransitionAsync(policy.Id, v1.Id, LifecycleState.Retired, "tomb", "actor-1");

        var act = async () => await svc.TransitionAsync(
            policy.Id, v1.Id, LifecycleState.Active, "rezz", "actor-1");

        await act.Should().ThrowAsync<InvalidLifecycleTransitionException>()
            .Where(e => e.From == LifecycleState.Retired && e.To == LifecycleState.Active);
    }

    [Fact]
    public async Task TransitionAsync_EmptyRationale_ThrowsValidationException()
    {
        var (svc, db, _) = NewService();
        var (policy, v1) = await SeedDraftAsync(db, "no-rationale");

        var act = async () => await svc.TransitionAsync(
            policy.Id, v1.Id, LifecycleState.Active, "  ", "actor-1");

        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*Rationale*");
    }

    [Fact]
    public async Task TransitionAsync_VersionNotFound_ThrowsNotFoundException()
    {
        var (svc, db, _) = NewService();
        var (policy, _) = await SeedDraftAsync(db, "missing-target");

        var act = async () => await svc.TransitionAsync(
            policy.Id, Guid.NewGuid(), LifecycleState.Active, "go", "actor-1");

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // P7.3 (#55) — author-cannot-self-approve invariant on publish.

    [Fact]
    public async Task TransitionAsync_PublishWithActorMatchingProposer_ThrowsPublishSelfApprovalException()
    {
        var (svc, db, events) = NewService();
        var (policy, draft) = await SeedDraftAsync(db, "pub-self-blocked");
        draft.ProposerSubjectId = "user:alice";
        await db.SaveChangesAsync();

        var act = async () => await svc.TransitionAsync(
            policy.Id, draft.Id, LifecycleState.Active, "self-publish", "user:alice");

        var assertion = await act.Should().ThrowAsync<PublishSelfApprovalException>();
        assertion.Which.PolicyVersionId.Should().Be(draft.Id);
        assertion.Which.SubjectId.Should().Be("user:alice");

        // No state mutation, no events dispatched.
        var reloaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == draft.Id);
        reloaded.State.Should().Be(LifecycleState.Draft);
        reloaded.PublishedAt.Should().BeNull();
        events.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task TransitionAsync_PublishWithDifferentActor_FlipsToActive()
    {
        var (svc, db, _) = NewService();
        var (policy, draft) = await SeedDraftAsync(db, "pub-distinct-actor");
        draft.ProposerSubjectId = "user:alice";
        await db.SaveChangesAsync();

        var dto = await svc.TransitionAsync(
            policy.Id, draft.Id, LifecycleState.Active, "go-live", "user:bob");

        dto.State.Should().Be("Active");
        var reloaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == draft.Id);
        reloaded.State.Should().Be(LifecycleState.Active);
        reloaded.PublishedBySubjectId.Should().Be("user:bob");
    }

    [Fact]
    public async Task TransitionAsync_PublishOnNonDraft_ThrowsInvalidLifecycleBeforeSelfApproval()
    {
        // Publish-on-non-Draft must surface the matrix error first, not
        // the self-approval one — the lifecycle gate runs before the
        // domain invariant. Use a Retired version with proposer == actor;
        // the matrix-only result keeps the error contract stable for
        // upstream callers (P7.4 handler tests rely on this ordering).
        var (svc, db, _) = NewService();
        var (policy, _) = await SeedDraftAsync(db, "pub-on-retired");
        var retired = PolicyBuilders.AVersion(policy.Id, number: 2, state: LifecycleState.Retired);
        retired.ProposerSubjectId = "user:alice";
        db.PolicyVersions.Add(retired);
        await db.SaveChangesAsync();

        var act = async () => await svc.TransitionAsync(
            policy.Id, retired.Id, LifecycleState.Active, "rezz", "user:alice");

        await act.Should().ThrowAsync<InvalidLifecycleTransitionException>();
    }

    [Fact]
    public async Task TransitionAsync_WindDownByProposer_DoesNotApplySelfApprovalCheck()
    {
        // The self-approval guard is publish-specific. WindingDown and
        // Retire are administrative hygiene transitions and run even
        // when actor == proposer.
        var (svc, db, _) = NewService();
        var (policy, draft) = await SeedDraftAsync(db, "winddown-by-proposer");
        draft.ProposerSubjectId = "user:alice";
        await db.SaveChangesAsync();
        await svc.TransitionAsync(
            policy.Id, draft.Id, LifecycleState.Active, "go-live", "user:bob");

        var dto = await svc.TransitionAsync(
            policy.Id, draft.Id, LifecycleState.WindingDown, "sunset", "user:alice");

        dto.State.Should().Be("WindingDown");
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
}
