// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Response envelope for <c>GET /api/bindings/resolve</c> (P3.4, story
/// rivoli-ai/andy-policies#22). Carries the requested target so callers
/// caching the response can key on <c>(TargetType, TargetRef)</c>
/// without re-parsing their own input. <see cref="Count"/> mirrors
/// <c>Bindings.Count</c> for paginating callers that read the count
/// header field before the array.
/// </summary>
public sealed record ResolveBindingsResponse(
    BindingTargetType TargetType,
    string TargetRef,
    IReadOnlyList<ResolvedBindingDto> Bindings,
    int Count);
