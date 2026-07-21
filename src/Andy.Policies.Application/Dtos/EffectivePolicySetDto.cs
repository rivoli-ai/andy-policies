// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Envelope for <c>IBindingResolutionService</c> results (P4.3, story
/// rivoli-ai/andy-policies#30). <see cref="ScopeNodeId"/> is null when
/// the resolver was called with a target that does not map to a known
/// <c>ScopeNode</c> — the service then degrades to P3 exact-match
/// semantics rather than returning 404.
/// </summary>
public sealed record EffectivePolicySetDto(
    Guid? ScopeNodeId,
    IReadOnlyList<EffectivePolicyDto> Policies)
{
    /// <summary>Deterministic explanation of every override that changed
    /// the baseline set, including Exempt entries whose policy is absent
    /// from <see cref="Policies"/>.</summary>
    public IReadOnlyList<AppliedOverrideDto> AppliedOverrides { get; init; } =
        Array.Empty<AppliedOverrideDto>();
}

public sealed record AppliedOverrideDto(
    Guid OverrideId,
    OverrideEffect Effect,
    OverrideScopeKind ScopeKind,
    string ScopeRef,
    Guid OriginalPolicyVersionId,
    Guid? ReplacementPolicyVersionId);
