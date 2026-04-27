// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Data;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Events;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Implements the lifecycle state machine for <see cref="PolicyVersion"/>
/// (P2.2, #12). Every transition runs inside a serializable transaction and
/// dispatches in-process domain events post-commit. The unique partial
/// index <c>ix_policy_versions_one_active_per_policy</c> from P1.1 plus the
/// serializable isolation level together guarantee at most one version is
/// ever in <see cref="LifecycleState.Active"/> per policy.
/// </summary>
public sealed class LifecycleTransitionService : ILifecycleTransitionService
{
    /// <summary>
    /// Canonical transition matrix. Anything not listed here returns
    /// <c>false</c> from <see cref="IsTransitionAllowed"/> and throws
    /// <see cref="InvalidLifecycleTransitionException"/> from
    /// <see cref="TransitionAsync"/>. Self-transitions (<c>Active -&gt; Active</c>
    /// etc.) are deliberately absent.
    /// </summary>
    private static readonly LifecycleTransitionRule[] Matrix =
    {
        new(LifecycleState.Draft, LifecycleState.Active, "Publish"),
        new(LifecycleState.Active, LifecycleState.WindingDown, "WindDown"),
        new(LifecycleState.Active, LifecycleState.Retired, "Retire"),
        new(LifecycleState.WindingDown, LifecycleState.Retired, "Retire"),
    };

    private readonly AppDbContext _db;
    private readonly IRationalePolicy _rationale;
    private readonly IDomainEventDispatcher _events;
    private readonly TimeProvider _clock;

    public LifecycleTransitionService(
        AppDbContext db,
        IRationalePolicy rationale,
        IDomainEventDispatcher events,
        TimeProvider clock)
    {
        _db = db;
        _rationale = rationale;
        _events = events;
        _clock = clock;
    }

    public bool IsTransitionAllowed(LifecycleState from, LifecycleState to)
    {
        foreach (var rule in Matrix)
        {
            if (rule.From == from && rule.To == to)
            {
                return true;
            }
        }
        return false;
    }

    public IReadOnlyList<LifecycleTransitionRule> GetMatrix() => Matrix;

    public async Task<PolicyVersionDto> TransitionAsync(
        Guid policyId,
        Guid versionId,
        LifecycleState target,
        string rationale,
        string actorSubjectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(actorSubjectId);

        var rationaleError = _rationale.ValidateRationale(rationale);
        if (rationaleError is not null)
        {
            throw new RationaleRequiredException(rationaleError);
        }

        // Serializable on Postgres; SQLite EF maps Serializable to BEGIN IMMEDIATE
        // which acquires a reserved lock — adequate for the single-writer model.
        var pendingPublished = (PolicyVersionPublished?)null;
        var pendingSuperseded = (PolicyVersionSuperseded?)null;
        var pendingRetired = (PolicyVersionRetired?)null;
        PolicyVersion result;

        await using var transaction = await _db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        var version = await _db.PolicyVersions
            .FirstOrDefaultAsync(v => v.PolicyId == policyId && v.Id == versionId, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                $"PolicyVersion {versionId} not found under policy {policyId}.");

        if (!IsTransitionAllowed(version.State, target))
        {
            throw new InvalidLifecycleTransitionException(version.State, target);
        }

        var now = _clock.GetUtcNow();

        switch (target)
        {
            case LifecycleState.Active:
            {
                // Auto-supersede the existing Active version, if any. We rely on
                // the partial unique index to catch concurrent racers later.
                var previousActive = await _db.PolicyVersions
                    .FirstOrDefaultAsync(
                        v => v.PolicyId == policyId
                             && v.State == LifecycleState.Active
                             && v.Id != versionId,
                        ct)
                    .ConfigureAwait(false);

                if (previousActive is not null)
                {
                    // Issue the WindingDown UPDATE in its own SaveChanges so the
                    // partial unique index on (PolicyId) WHERE State = 'Active'
                    // sees the row leave the Active set before the new version's
                    // UPDATE adds the successor. EF batches updates by tracking
                    // order, which on the loader path above puts `version`
                    // (loaded first) ahead of `previousActive` (loaded second);
                    // SQLite then trips the unique index on the still-Active v1
                    // when v2's UPDATE lands first. Splitting the writes inside
                    // the open serializable transaction preserves atomicity
                    // without depending on EF's update-ordering heuristics.
                    previousActive.State = LifecycleState.WindingDown;
                    previousActive.SupersededByVersionId = version.Id;
                    pendingSuperseded = new PolicyVersionSuperseded(
                        policyId, previousActive.Id, version.Id, now);
                    await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                version.State = LifecycleState.Active;
                version.PublishedAt = now;
                version.PublishedBySubjectId = actorSubjectId;
                pendingPublished = new PolicyVersionPublished(
                    policyId, version.Id, version.Version, actorSubjectId, rationale, now);
                break;
            }

            case LifecycleState.WindingDown:
            {
                version.State = LifecycleState.WindingDown;
                break;
            }

            case LifecycleState.Retired:
            {
                version.State = LifecycleState.Retired;
                version.RetiredAt = now;
                pendingRetired = new PolicyVersionRetired(
                    policyId, version.Id, actorSubjectId, rationale, now);
                break;
            }

            default:
                // The matrix already rejected unknown targets, so this branch
                // is unreachable — guard so a future enum value can't sneak in.
                throw new InvalidLifecycleTransitionException(version.State, target);
        }

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsConcurrentPublishSignal(ex))
        {
            throw new ConcurrentPublishException(policyId, ex);
        }
        catch (InvalidOperationException ex)
            when (ex.InnerException is DbUpdateException inner && IsConcurrentPublishSignal(inner))
        {
            // Npgsql wraps the serialization-failure (SQLSTATE 40001) inside
            // EF's retry execution strategy, which surfaces as
            // InvalidOperationException("transient failure"). Unwrap so the
            // caller sees the same 409-mappable exception as the unique-index
            // path.
            throw new ConcurrentPublishException(policyId, ex.InnerException);
        }

        result = version;

        // Post-commit dispatch. Per the contract, handler errors are swallowed
        // by the dispatcher — never roll back here. Order matters: Superseded
        // before Published so subscribers can pair (old, new) atomically.
        if (pendingSuperseded is not null)
        {
            await _events.DispatchAsync(pendingSuperseded, ct).ConfigureAwait(false);
        }
        if (pendingPublished is not null)
        {
            await _events.DispatchAsync(pendingPublished, ct).ConfigureAwait(false);
        }
        if (pendingRetired is not null)
        {
            await _events.DispatchAsync(pendingRetired, ct).ConfigureAwait(false);
        }

        return ToVersionDto(result);
    }

    /// <summary>
    /// Detect the two provider signals that indicate a concurrent-publish
    /// race lost. Postgres can surface either:
    /// <list type="bullet">
    ///   <item>SQLSTATE 23505 (unique_violation) — the partial-active index
    ///     rejected the second activation</item>
    ///   <item>SQLSTATE 40001 (serialization_failure) — the serializable
    ///     isolation level detected concurrent updates to the same row</item>
    /// </list>
    /// SQLite surfaces as "UNIQUE constraint failed" via SQLite error 19.
    /// Matching on inner-exception message is brittle but adequate
    /// cross-provider — Npgsql does not expose the SqlState code through a
    /// stable typed field across versions.
    /// </summary>
    private static bool IsConcurrentPublishSignal(DbUpdateException ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("23505", StringComparison.Ordinal)
            || msg.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("40001", StringComparison.Ordinal)
            || msg.Contains("could not serialize", StringComparison.OrdinalIgnoreCase);
    }

    private static PolicyVersionDto ToVersionDto(PolicyVersion v) => new(
        v.Id,
        v.PolicyId,
        v.Version,
        v.State.ToString(),
        ToEnforcementWire(v.Enforcement),
        ToSeverityWire(v.Severity),
        v.Scopes.ToArray(),
        v.Summary,
        v.RulesJson,
        v.CreatedAt,
        v.CreatedBySubjectId,
        v.ProposerSubjectId);

    private static string ToEnforcementWire(EnforcementLevel level) => level switch
    {
        EnforcementLevel.May => "MAY",
        EnforcementLevel.Should => "SHOULD",
        EnforcementLevel.Must => "MUST",
        _ => throw new InvalidOperationException($"Unknown EnforcementLevel: {level}"),
    };

    private static string ToSeverityWire(Severity severity) => severity switch
    {
        Severity.Info => "info",
        Severity.Moderate => "moderate",
        Severity.Critical => "critical",
        _ => throw new InvalidOperationException($"Unknown Severity: {severity}"),
    };
}
