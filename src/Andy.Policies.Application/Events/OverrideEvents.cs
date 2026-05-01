// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Events;

/// <summary>
/// Emitted in-process when an <c>Override</c> is proposed (P5.2,
/// rivoli-ai/andy-policies#52). P6 audit subscribes; cross-service
/// signalling is the consumer's responsibility per the P5 non-goals.
/// </summary>
public sealed record OverrideProposed(
    Guid OverrideId,
    Guid PolicyVersionId,
    OverrideScopeKind ScopeKind,
    string ScopeRef,
    OverrideEffect Effect,
    string ProposerSubjectId,
    DateTimeOffset At);

/// <summary>
/// Emitted in-process when an <c>Override</c> transitions from
/// <see cref="OverrideState.Proposed"/> to <see cref="OverrideState.Approved"/>.
/// The approver is guaranteed to differ from the proposer
/// (P5.2 service enforces it before the transition commits).
/// </summary>
public sealed record OverrideApproved(
    Guid OverrideId,
    Guid PolicyVersionId,
    string ApproverSubjectId,
    string ProposerSubjectId,
    DateTimeOffset At);

/// <summary>
/// Emitted in-process when an <c>Override</c> is explicitly revoked
/// before expiry. The reaper-driven Expired transition will emit a
/// separate <c>OverrideExpired</c> event when P5.3 lands.
/// </summary>
public sealed record OverrideRevoked(
    Guid OverrideId,
    Guid PolicyVersionId,
    string ActorSubjectId,
    string Reason,
    DateTimeOffset At);
