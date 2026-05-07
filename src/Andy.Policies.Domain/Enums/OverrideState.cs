// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Enums;

/// <summary>
/// Lifecycle state of an <see cref="Entities.Override"/> (P5.1,
/// story rivoli-ai/andy-policies#49). Five-state machine:
/// <list type="bullet">
///   <item><see cref="Proposed"/> — created by an author; awaiting
///     approver. <c>ApproverSubjectId</c> + <c>ApprovedAt</c> are
///     null in this state.</item>
///   <item><see cref="Approved"/> — committed by an approver
///     (P5.2 enforces approver != proposer). The override is in
///     force until <c>ExpiresAt</c> or revocation.</item>
///   <item><see cref="Revoked"/> — explicitly revoked by an
///     approver before expiry; carries a non-null
///     <c>RevocationReason</c>.</item>
///   <item><see cref="Expired"/> — the reaper (P5.3) flipped the
///     row when <c>ExpiresAt</c> passed; transition is automatic
///     and irreversible.</item>
///   <item><see cref="Rejected"/> — explicitly rejected by an
///     approver while still in <c>Proposed</c> (P9 follow-up #201,
///     2026-05-07). Distinct from <see cref="Revoked"/> so the audit
///     chain can tell "never approved" from "was approved, then
///     pulled". Carries a non-null <c>RevocationReason</c> (reused
///     column — the rejection reason lives there).</item>
/// </list>
/// Persisted as <c>string</c> so the partial index
/// <c>ix_overrides_expiry_approved</c> can filter on
/// <c>"State" = 'Approved'</c> directly without an int-to-string
/// cast.
/// </summary>
public enum OverrideState
{
    Proposed = 0,
    Approved = 1,
    Revoked = 2,
    Expired = 3,
    Rejected = 4,
}
