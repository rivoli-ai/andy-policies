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
/// </list>
/// The reaper (P5.3) is the only path into
/// <see cref="OverrideState.Expired"/>.
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
