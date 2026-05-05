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
/// EF-backed <see cref="IOverrideService"/> implementation (P5.2,
/// story rivoli-ai/andy-policies#52). Drives the four-state machine
/// from P5.1 (Proposed → Approved → Revoked|Expired) with
/// serializable-transaction state transitions, self-approval rejection,
/// and RBAC delegation to <see cref="IRbacChecker"/>.
/// </summary>
/// <remarks>
/// <para>
/// State transitions run inside a serializable EF transaction; the
/// optimistic <see cref="Override.Revision"/> token (bumped in
/// <c>AppDbContext.SaveChangesAsync</c>) catches concurrent racers
/// and surfaces them as <see cref="DbUpdateConcurrencyException"/>
/// — translated to HTTP 409 by the global exception handler.
/// </para>
/// <para>
/// The reaper (P5.3) is the only path into <see cref="OverrideState.Expired"/>;
/// this service deliberately doesn't expose an Expire transition.
/// </para>
/// </remarks>
public sealed class OverrideService : IOverrideService
{
    private const int MaxScopeRefLength = 256;
    private const int MaxRationaleLength = 2000;

    /// <summary>
    /// Minimum lifetime: an override that would expire within this
    /// window of <c>now</c> at propose time is rejected. Catches
    /// accidental same-second / past expiries that would leak through
    /// the reaper before any consumer can observe the override.
    /// </summary>
    private static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(1);

    private readonly AppDbContext _db;
    private readonly IRbacChecker _rbac;
    private readonly IDomainEventDispatcher _events;
    private readonly TimeProvider _clock;

    public OverrideService(
        AppDbContext db,
        IRbacChecker rbac,
        IDomainEventDispatcher events,
        TimeProvider clock)
    {
        _db = db;
        _rbac = rbac;
        _events = events;
        _clock = clock;
    }

    public async Task<OverrideDto> ProposeAsync(
        ProposeOverrideRequest request,
        string proposerSubjectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(proposerSubjectId);

        var scopeRef = (request.ScopeRef ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(scopeRef))
        {
            throw new ValidationException("ScopeRef is required and may not be empty or whitespace.");
        }
        if (scopeRef.Length > MaxScopeRefLength)
        {
            throw new ValidationException(
                $"ScopeRef length {scopeRef.Length} exceeds the {MaxScopeRefLength}-char limit.");
        }
        var rationale = (request.Rationale ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(rationale))
        {
            throw new ValidationException("Rationale is required and may not be empty or whitespace.");
        }
        if (rationale.Length > MaxRationaleLength)
        {
            throw new ValidationException(
                $"Rationale length {rationale.Length} exceeds the {MaxRationaleLength}-char limit.");
        }

        // Effect ↔ Replacement invariant. The DB CHECK from P5.1 is the
        // belt; this is the braces (and produces a structured 400
        // instead of a DbUpdateException).
        if (request.Effect == OverrideEffect.Replace && request.ReplacementPolicyVersionId is null)
        {
            throw new ValidationException(
                "Effect=Replace requires a non-null ReplacementPolicyVersionId.");
        }
        if (request.Effect == OverrideEffect.Exempt && request.ReplacementPolicyVersionId is not null)
        {
            throw new ValidationException(
                "Effect=Exempt requires a null ReplacementPolicyVersionId.");
        }

        var now = _clock.GetUtcNow();
        if (request.ExpiresAt <= now + MinimumLifetime)
        {
            throw new ValidationException(
                $"ExpiresAt must be at least {MinimumLifetime.TotalMinutes:n0} minute(s) in the future.");
        }

        // Both PolicyVersion references must exist and be bindable
        // (Active or WindingDown — see P3.2 BindingService for the
        // same Retired-refusal posture).
        var primaryVersion = await _db.PolicyVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == request.PolicyVersionId, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"PolicyVersion {request.PolicyVersionId} not found.");
        if (primaryVersion.State == LifecycleState.Retired)
        {
            throw new ValidationException(
                $"Cannot propose override against retired PolicyVersion {primaryVersion.Id}.");
        }
        if (request.ReplacementPolicyVersionId is { } replacementId)
        {
            var replacementVersion = await _db.PolicyVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == replacementId, ct)
                .ConfigureAwait(false)
                ?? throw new NotFoundException($"Replacement PolicyVersion {replacementId} not found.");
            if (replacementVersion.State == LifecycleState.Retired)
            {
                throw new ValidationException(
                    $"Cannot propose override using retired replacement PolicyVersion {replacementId}.");
            }
        }

        var ovr = new Override
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = request.PolicyVersionId,
            ScopeKind = request.ScopeKind,
            ScopeRef = scopeRef,
            Effect = request.Effect,
            ReplacementPolicyVersionId = request.ReplacementPolicyVersionId,
            ProposerSubjectId = proposerSubjectId,
            ApproverSubjectId = null,
            State = OverrideState.Proposed,
            ProposedAt = now,
            ApprovedAt = null,
            ExpiresAt = request.ExpiresAt,
            Rationale = rationale,
            RevocationReason = null,
        };
        _db.Overrides.Add(ovr);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _events.DispatchAsync(new OverrideProposed(
            OverrideId: ovr.Id,
            PolicyVersionId: ovr.PolicyVersionId,
            ScopeKind: ovr.ScopeKind,
            ScopeRef: ovr.ScopeRef,
            Effect: ovr.Effect,
            ProposerSubjectId: ovr.ProposerSubjectId,
            At: now), ct).ConfigureAwait(false);

        return ToDto(ovr);
    }

    public async Task<OverrideDto> ApproveAsync(
        Guid id,
        string approverSubjectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(approverSubjectId);

        await using var transaction = await _db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        var ovr = await _db.Overrides
            .FirstOrDefaultAsync(o => o.Id == id, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Override {id} not found.");

        if (ovr.State != OverrideState.Proposed)
        {
            throw new ConflictException(
                $"Override {id} is in state {ovr.State}; only Proposed overrides can be approved.");
        }

        // Self-approval check fires *before* the RBAC delegation: an
        // approver who happens to also have the approve permission
        // still cannot rubber-stamp their own proposal.
        if (string.Equals(approverSubjectId, ovr.ProposerSubjectId, StringComparison.Ordinal))
        {
            throw new SelfApprovalException(id, approverSubjectId);
        }

        // RBAC: the ScopeRef doubles as the resource-instance id so
        // operators can grant per-cohort or per-principal approval
        // rights via andy-rbac scoping (P7.2 #51). Empty groups list
        // until P7.4 (#57) plumbs the JWT groups claim through the
        // controller → service call chain.
        var rbacResult = await _rbac.CheckAsync(
            approverSubjectId,
            permissionCode: "andy-policies:override:approve",
            groups: Array.Empty<string>(),
            resourceInstanceId: ovr.ScopeRef,
            ct).ConfigureAwait(false);
        if (!rbacResult.Allowed)
        {
            throw new RbacDeniedException(
                approverSubjectId, "andy-policies:override:approve", ovr.ScopeRef, rbacResult.Reason);
        }

        var now = _clock.GetUtcNow();
        ovr.State = OverrideState.Approved;
        ovr.ApproverSubjectId = approverSubjectId;
        ovr.ApprovedAt = now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        await _events.DispatchAsync(new OverrideApproved(
            OverrideId: ovr.Id,
            PolicyVersionId: ovr.PolicyVersionId,
            ApproverSubjectId: approverSubjectId,
            ProposerSubjectId: ovr.ProposerSubjectId,
            At: now), ct).ConfigureAwait(false);

        return ToDto(ovr);
    }

    public async Task<OverrideDto> RevokeAsync(
        Guid id,
        RevokeOverrideRequest request,
        string actorSubjectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(actorSubjectId);

        var reason = (request.RevocationReason ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(reason))
        {
            throw new ValidationException("RevocationReason is required and may not be empty or whitespace.");
        }
        if (reason.Length > MaxRationaleLength)
        {
            throw new ValidationException(
                $"RevocationReason length {reason.Length} exceeds the {MaxRationaleLength}-char limit.");
        }

        await using var transaction = await _db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        var ovr = await _db.Overrides
            .FirstOrDefaultAsync(o => o.Id == id, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Override {id} not found.");

        if (ovr.State is not (OverrideState.Proposed or OverrideState.Approved))
        {
            throw new ConflictException(
                $"Override {id} is in terminal state {ovr.State}; only Proposed or Approved overrides can be revoked.");
        }

        // RBAC: revoke is a separate permission so admins can grant
        // approve + revoke independently (e.g. to a security on-call
        // role that should be able to revoke but not approve). Empty
        // groups list until P7.4 (#57) plumbs the JWT groups claim
        // through the controller → service call chain.
        var rbacResult = await _rbac.CheckAsync(
            actorSubjectId,
            permissionCode: "andy-policies:override:revoke",
            groups: Array.Empty<string>(),
            resourceInstanceId: ovr.ScopeRef,
            ct).ConfigureAwait(false);
        if (!rbacResult.Allowed)
        {
            throw new RbacDeniedException(
                actorSubjectId, "andy-policies:override:revoke", ovr.ScopeRef, rbacResult.Reason);
        }

        var now = _clock.GetUtcNow();
        ovr.State = OverrideState.Revoked;
        ovr.RevocationReason = reason;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        await _events.DispatchAsync(new OverrideRevoked(
            OverrideId: ovr.Id,
            PolicyVersionId: ovr.PolicyVersionId,
            ActorSubjectId: actorSubjectId,
            Reason: reason,
            At: now), ct).ConfigureAwait(false);

        return ToDto(ovr);
    }

    public async Task<OverrideDto> ExpireAsync(Guid id, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        var ovr = await _db.Overrides
            .FirstOrDefaultAsync(o => o.Id == id, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Override {id} not found.");

        if (ovr.State != OverrideState.Approved)
        {
            // Race tolerance: if the reaper picked up an id that another
            // actor revoked between the scan and this call, the conflict
            // here is the reaper's signal to skip the row. The hosted
            // service catches ConflictException and continues.
            throw new ConflictException(
                $"Override {id} is in state {ovr.State}; only Approved overrides can be expired.");
        }

        var now = _clock.GetUtcNow();
        if (ovr.ExpiresAt > now)
        {
            // Belt-and-braces: protects against operator/test fixtures
            // that bump ExpiresAt forward between scan and expire.
            throw new ConflictException(
                $"Override {id} is not yet due (ExpiresAt={ovr.ExpiresAt:o}, now={now:o}).");
        }

        ovr.State = OverrideState.Expired;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        await _events.DispatchAsync(new OverrideExpired(
            OverrideId: ovr.Id,
            PolicyVersionId: ovr.PolicyVersionId,
            At: now), ct).ConfigureAwait(false);

        return ToDto(ovr);
    }

    public async Task<OverrideDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var ovr = await _db.Overrides.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, ct)
            .ConfigureAwait(false);
        return ovr is null ? null : ToDto(ovr);
    }

    public async Task<IReadOnlyList<OverrideDto>> ListAsync(
        OverrideListFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _db.Overrides.AsNoTracking().AsQueryable();
        if (filter.State is { } state) query = query.Where(o => o.State == state);
        if (filter.ScopeKind is { } scopeKind) query = query.Where(o => o.ScopeKind == scopeKind);
        if (!string.IsNullOrEmpty(filter.ScopeRef)) query = query.Where(o => o.ScopeRef == filter.ScopeRef);
        if (filter.PolicyVersionId is { } pvid) query = query.Where(o => o.PolicyVersionId == pvid);

        var rows = await query.ToListAsync(ct).ConfigureAwait(false);
        return rows
            .OrderByDescending(o => o.ProposedAt)
            .Select(ToDto)
            .ToList();
    }

    public async Task<IReadOnlyList<OverrideDto>> GetActiveAsync(
        OverrideScopeKind scopeKind,
        string scopeRef,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(scopeRef);

        var now = _clock.GetUtcNow();
        // Filter on (ScopeKind, ScopeRef, State) server-side (covered
        // by ix_overrides_scope_state) and refine on ExpiresAt + order
        // client-side. SQLite cannot translate DateTimeOffset
        // comparisons or ordering — same posture as the rest of the
        // codebase (see PolicyService list filters and the
        // OverrideExpiryReaper sweep).
        var rows = await _db.Overrides.AsNoTracking()
            .Where(o => o.ScopeKind == scopeKind
                        && o.ScopeRef == scopeRef
                        && o.State == OverrideState.Approved)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows
            .Where(o => o.ExpiresAt > now)
            .OrderBy(o => o.ApprovedAt)
            .Select(ToDto)
            .ToList();
    }

    private static OverrideDto ToDto(Override o) => new(
        o.Id,
        o.PolicyVersionId,
        o.ScopeKind,
        o.ScopeRef,
        o.Effect,
        o.ReplacementPolicyVersionId,
        o.ProposerSubjectId,
        o.ApproverSubjectId,
        o.State,
        o.ProposedAt,
        o.ApprovedAt,
        o.ExpiresAt,
        o.Rationale,
        o.RevocationReason);
}
