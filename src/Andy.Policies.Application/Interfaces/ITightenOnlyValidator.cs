// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Write-path enforcement of stricter-tightens-only (P4.4, story
/// rivoli-ai/andy-policies#32). Companion to the read-time fold in
/// <see cref="IBindingResolutionService"/> from P4.3 — the read path
/// silently drops would-be downgrades; this interface refuses to
/// commit them in the first place so the catalog never accumulates
/// unreachable rows.
/// </summary>
/// <remarks>
/// <para>
/// Returns <c>null</c> for the allowed shape. Returns a populated
/// <see cref="TightenViolation"/> when the proposed change would
/// loosen a Mandatory binding declared upstream — callers translate
/// that into <see cref="Exceptions.TightenOnlyViolationException"/>
/// and the API layer maps to HTTP 409.
/// </para>
/// <para>
/// <see cref="ValidateDeleteAsync"/> is a null-returning hook. Per
/// the issue's reviewer-flagged reconciliation, tighten-only is a
/// CREATE-time invariant only — a delete cannot produce a weaker
/// downstream binding (it can only remove one). The hook stays for
/// P5 overrides and P6 audit to attach side-effect checks later.
/// </para>
/// </remarks>
public interface ITightenOnlyValidator
{
    Task<TightenViolation?> ValidateCreateAsync(
        Guid policyVersionId,
        BindingTargetType targetType,
        string targetRef,
        BindStrength bindStrength,
        CancellationToken ct = default);

    Task<TightenViolation?> ValidateDeleteAsync(
        Guid bindingId,
        CancellationToken ct = default);
}

/// <summary>
/// Describes which ancestor binding blocked the proposed write
/// (P4.4). Carries enough information for the REST/MCP/gRPC error
/// envelope to point an admin at the conflict without a follow-up
/// query.
/// </summary>
public sealed record TightenViolation(
    Guid OffendingAncestorBindingId,
    Guid OffendingScopeNodeId,
    string OffendingScopeDisplayName,
    string PolicyKey,
    string Reason);
