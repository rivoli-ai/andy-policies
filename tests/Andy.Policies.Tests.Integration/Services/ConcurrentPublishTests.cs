// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Andy.Policies.Tests.Integration.Services;

/// <summary>
/// P2.2 (#12) acceptance: two concurrent <c>Draft -&gt; Active</c> attempts
/// for the same policy must leave exactly one row in <see cref="LifecycleState.Active"/>.
/// The unique partial index on <c>(PolicyId) WHERE State = 'Active'</c> from
/// P1.1 plus the serializable transaction in
/// <see cref="LifecycleTransitionService"/> together guarantee the loser
/// surfaces as <see cref="ConcurrentPublishException"/>.
///
/// Skipped silently when Docker is unavailable.
/// </summary>
public class ConcurrentPublishTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("andy_policies_concurrent")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
            _dockerAvailable = true;

            await using var setup = NewContext();
            await setup.Database.MigrateAsync();
        }
        catch (Exception)
        {
            _dockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    [SkippableFact]
    public async Task FiftyConcurrentPublishes_OfSameDraft_LeaveExactlyOneActive()
    {
        // P2.8 (#18) headline invariant: under 50-way concurrent contention,
        // exactly one transition commits and the other 49 surface a 409-mappable
        // exception. The N=2 scaffold above proves the invariant; this
        // larger N catches any bias in the lock-acquisition ordering and
        // exercises the 23505 / 40001 detection paths in
        // IsConcurrentPublishSignal under real load. We can't stage 50
        // distinct drafts of the same policy because of the
        // ix_policy_versions_one_draft_per_policy partial unique index, so
        // the realistic shape is N tasks racing for the same draft id —
        // identical to a thundering-herd retry storm in production.
        Skip.IfNot(_dockerAvailable);

        const int N = 50;

        Guid policyId, draftId;
        await using (var seed = NewContext())
        {
            var policy = new Policy
            {
                Id = Guid.NewGuid(),
                Name = $"concurrent-50-{Guid.NewGuid():N}",
                CreatedBySubjectId = "seed",
            };
            var draft = MakeDraft(policy.Id, 1);
            seed.Policies.Add(policy);
            seed.PolicyVersions.Add(draft);
            await seed.SaveChangesAsync();
            policyId = policy.Id;
            draftId = draft.Id;
        }

        Task<Outcome> RunAsync(int i) => Task.Run(async () =>
        {
            await using var db = NewContext();
            var service = new LifecycleTransitionService(
                db,
                new RequireNonEmptyRationalePolicy(),
                new NoopDispatcher(),
                TimeProvider.System);
            try
            {
                await service.TransitionAsync(
                    policyId, draftId, LifecycleState.Active, $"burst-{i}", $"actor-{i}");
                return Outcome.Success;
            }
            catch (ConcurrentPublishException) { return Outcome.LostRace; }
            catch (InvalidLifecycleTransitionException) { return Outcome.LostRace; }
        });

        var tasks = Enumerable.Range(0, N).Select(RunAsync);
        var results = await Task.WhenAll(tasks);

        results.Count(r => r == Outcome.Success).Should().Be(1,
            "exactly one of the N concurrent publishers must commit");
        results.Count(r => r == Outcome.LostRace).Should().Be(N - 1);

        await using var verify = NewContext();
        var actives = await verify.PolicyVersions
            .AsNoTracking()
            .Where(v => v.PolicyId == policyId && v.State == LifecycleState.Active)
            .ToListAsync();
        actives.Should().ContainSingle().Which.Id.Should().Be(draftId);
    }

    [SkippableFact]
    public async Task TwoConcurrentPublishes_OfSameDraft_LeaveExactlyOneActive()
    {
        Skip.IfNot(_dockerAvailable);

        // P1's `ix_policy_versions_one_draft_per_policy` partial unique index
        // makes "two parallel drafts" impossible by design — the realistic
        // contention scenario is two API replicas attempting to publish the
        // *same* draft simultaneously (e.g. retried HTTP request + a separate
        // operator click). Exactly one transition must commit; the other must
        // surface a 409-mappable exception so the caller knows to re-read.
        Guid policyId, draftId;
        await using (var seed = NewContext())
        {
            var policy = new Policy
            {
                Id = Guid.NewGuid(),
                Name = $"concurrent-{Guid.NewGuid():N}",
                CreatedBySubjectId = "seed",
            };
            var draft = MakeDraft(policy.Id, 1);
            seed.Policies.Add(policy);
            seed.PolicyVersions.Add(draft);
            await seed.SaveChangesAsync();
            policyId = policy.Id;
            draftId = draft.Id;
        }

        // Independent service instances + DbContexts per task — exactly the
        // shape two API replicas would have in production.
        Task<Outcome> RunAsync(string actor) => Task.Run(async () =>
        {
            await using var db = NewContext();
            var service = new LifecycleTransitionService(
                db,
                new RequireNonEmptyRationalePolicy(),
                new NoopDispatcher(),
                TimeProvider.System);
            try
            {
                await service.TransitionAsync(
                    policyId, draftId, LifecycleState.Active, "race", actor);
                return Outcome.Success;
            }
            catch (ConcurrentPublishException)
            {
                return Outcome.LostRace;
            }
            catch (InvalidLifecycleTransitionException)
            {
                // The loser observes the draft has already moved to Active by
                // the time it reloads; the matrix check rejects Active -> Active.
                // Either failure mode is an acceptable "lost the race" signal —
                // both translate to HTTP 409 in the controller layer.
                return Outcome.LostRace;
            }
        });

        var results = await Task.WhenAll(
            RunAsync("actor-a"),
            RunAsync("actor-b"));

        results.Count(r => r == Outcome.Success).Should().Be(1);
        results.Count(r => r == Outcome.LostRace).Should().Be(1);

        await using var verify = NewContext();
        var actives = await verify.PolicyVersions
            .AsNoTracking()
            .Where(v => v.PolicyId == policyId && v.State == LifecycleState.Active)
            .ToListAsync();
        actives.Should().ContainSingle().Which.Id.Should().Be(draftId);
    }

    private static PolicyVersion MakeDraft(Guid policyId, int version) => new()
    {
        Id = Guid.NewGuid(),
        PolicyId = policyId,
        Version = version,
        State = LifecycleState.Draft,
        Enforcement = EnforcementLevel.Should,
        Severity = Severity.Moderate,
        Scopes = new List<string>(),
        Summary = $"v{version}",
        RulesJson = "{}",
        CreatedBySubjectId = "seed",
        ProposerSubjectId = "seed",
    };

    private enum Outcome
    {
        Success,
        LostRace,
    }

    private sealed class NoopDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
            where TEvent : notnull => Task.CompletedTask;
    }
}
