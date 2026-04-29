// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

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
    IReadOnlyList<EffectivePolicyDto> Policies);
