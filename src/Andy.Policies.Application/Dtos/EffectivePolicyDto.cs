// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// One entry in an effective-policy set (P4.3, story
/// rivoli-ai/andy-policies#30). The result of stricter-tightens-only
/// resolution: each <see cref="PolicyId"/> appears at most once,
/// carrying the strictest <see cref="BindStrength"/> seen anywhere in
/// the scope chain plus a pointer back to the binding (and the scope
/// node that bound it) that won the resolution.
/// </summary>
public sealed record EffectivePolicyDto(
    Guid PolicyId,
    Guid PolicyVersionId,
    string PolicyKey,
    int Version,
    BindStrength BindStrength,
    Guid SourceBindingId,
    Guid? SourceScopeNodeId,
    ScopeType? SourceScopeType,
    int SourceDepth);
