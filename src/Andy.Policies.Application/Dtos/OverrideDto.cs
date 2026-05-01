// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Wire-format projection of an <c>Override</c> (P5.2, story
/// rivoli-ai/andy-policies#52). Surface controllers (REST P5.5,
/// MCP/gRPC/CLI in P5.6 / P5.7) emit this shape directly so wire
/// behaviour stays uniform across surfaces.
/// </summary>
public sealed record OverrideDto(
    Guid Id,
    Guid PolicyVersionId,
    OverrideScopeKind ScopeKind,
    string ScopeRef,
    OverrideEffect Effect,
    Guid? ReplacementPolicyVersionId,
    string ProposerSubjectId,
    string? ApproverSubjectId,
    OverrideState State,
    DateTimeOffset ProposedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset ExpiresAt,
    string Rationale,
    string? RevocationReason);
