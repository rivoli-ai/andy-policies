// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Request payload for <c>IOverrideService.ProposeAsync</c> (P5.2,
/// story rivoli-ai/andy-policies#52). The service validates that
/// <see cref="Effect"/> matches the presence of
/// <see cref="ReplacementPolicyVersionId"/>: <c>Replace</c> requires
/// non-null, <c>Exempt</c> requires null. Both
/// <see cref="PolicyVersionId"/> and (if present)
/// <see cref="ReplacementPolicyVersionId"/> must reference an
/// existing <c>PolicyVersion</c>.
/// </summary>
public sealed record ProposeOverrideRequest(
    Guid PolicyVersionId,
    OverrideScopeKind ScopeKind,
    string ScopeRef,
    OverrideEffect Effect,
    Guid? ReplacementPolicyVersionId,
    DateTimeOffset ExpiresAt,
    string Rationale);
