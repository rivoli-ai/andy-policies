// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Application service for <c>Override</c> mutation + read (P5.2,
/// story rivoli-ai/andy-policies#52). REST (P5.5), MCP (P5.6), gRPC
/// (P5.7), and CLI (P5.7) all delegate to this single interface —
/// surfaces never duplicate state-machine logic.
/// </summary>
/// <remarks>
/// The state machine pinned by ADR 0005 (lands with P5.9):
/// <list type="bullet">
///   <item><c>ProposeAsync</c> — inserts a row in
///     <see cref="OverrideState.Proposed"/>; <c>ApproverSubjectId</c>
///     stays null until <c>ApproveAsync</c>.</item>
///   <item><c>ApproveAsync</c> — transitions to
///     <see cref="OverrideState.Approved"/>; rejects self-approval
///     with <c>SelfApprovalException</c> and delegates RBAC to
///     <see cref="IRbacChecker"/>.</item>
///   <item><c>RevokeAsync</c> — transitions to
///     <see cref="OverrideState.Revoked"/> from either
///     <c>Proposed</c> or <c>Approved</c>; requires a non-empty
///     revocation reason.</item>
///   <item><c>ExpireAsync</c> — system-only transition into
///     <see cref="OverrideState.Expired"/>. Called exclusively by
///     <c>OverrideExpiryReaper</c> (P5.3); skips RBAC and is the only
///     code path into the <c>Expired</c> state.</item>
/// </list>
/// </remarks>
public interface IOverrideService
{
    Task<OverrideDto> ProposeAsync(
        ProposeOverrideRequest request,
        string proposerSubjectId,
        CancellationToken ct = default);

    Task<OverrideDto> ApproveAsync(
        Guid id,
        string approverSubjectId,
        CancellationToken ct = default);

    Task<OverrideDto> RevokeAsync(
        Guid id,
        RevokeOverrideRequest request,
        string actorSubjectId,
        CancellationToken ct = default);

    /// <summary>
    /// Reject a <c>Proposed</c> override (P9 follow-up #201, 2026-05-07).
    /// Distinct from <see cref="RevokeAsync"/>: rejection only fires
    /// from <c>Proposed</c>, terminating the proposal before it ever
    /// took effect; revocation fires from <c>Approved</c> (and
    /// continues to accept <c>Proposed</c> for backward compat). The
    /// audit trail can therefore distinguish "this proposal was
    /// declined" from "this active override was pulled". Self-rejection
    /// is allowed — proposers may withdraw their own proposals.
    /// </summary>
    /// <exception cref="Andy.Policies.Application.Exceptions.NotFoundException">
    /// No row matches <paramref name="id"/>.</exception>
    /// <exception cref="Andy.Policies.Application.Exceptions.ValidationException">
    /// Empty or oversized rejection reason.</exception>
    /// <exception cref="Andy.Policies.Application.Exceptions.ConflictException">
    /// Row is not in <see cref="OverrideState.Proposed"/>.</exception>
    Task<OverrideDto> RejectAsync(
        Guid id,
        RejectOverrideRequest request,
        string actorSubjectId,
        CancellationToken ct = default);

    /// <summary>
    /// System-only transition: moves an <c>Approved</c> override past
    /// its <c>ExpiresAt</c> into <see cref="OverrideState.Expired"/>.
    /// Called exclusively by <c>OverrideExpiryReaper</c> (P5.3,
    /// rivoli-ai/andy-policies#53). Skips RBAC because there is no
    /// human actor; emits <c>OverrideExpired</c> (distinct from
    /// <c>OverrideRevoked</c> so audit can record
    /// <c>actor=system:reaper</c>).
    /// </summary>
    /// <returns>The DTO of the newly-expired row.</returns>
    /// <exception cref="Andy.Policies.Application.Exceptions.NotFoundException">
    /// No row matches <paramref name="id"/>.</exception>
    /// <exception cref="Andy.Policies.Application.Exceptions.ConflictException">
    /// Row is not <c>Approved</c>, or its <c>ExpiresAt</c> is still in
    /// the future (i.e. the reaper raced an updated expiry).</exception>
    Task<OverrideDto> ExpireAsync(Guid id, CancellationToken ct = default);

    Task<OverrideDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<OverrideDto>> ListAsync(
        OverrideListFilter filter,
        CancellationToken ct = default);

    /// <summary>
    /// Returns currently-Approved overrides for a given
    /// (<paramref name="scopeKind"/>, <paramref name="scopeRef"/>)
    /// pair. Used by P4.3 chain resolution to apply overrides to the
    /// effective policy set; only Approved + non-expired rows
    /// surface here.
    /// </summary>
    Task<IReadOnlyList<OverrideDto>> GetActiveAsync(
        OverrideScopeKind scopeKind,
        string scopeRef,
        CancellationToken ct = default);
}
