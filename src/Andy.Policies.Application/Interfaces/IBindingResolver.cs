// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Resolution-shaped read over the binding table (P3.4, story
/// rivoli-ai/andy-policies#22). Distinct from
/// <c>IBindingService.ListByTargetAsync</c> in three ways:
/// <list type="bullet">
///   <item>Joins to <c>PolicyVersion</c> + <c>Policy</c> so callers get
///     the policy name, version state/dimension fields, and scopes
///     without a second round-trip.</item>
///   <item>Filters out bindings whose target version is in
///     <c>LifecycleState.Retired</c> — retired versions never appear in
///     resolve.</item>
///   <item>Dedups on <c>(PolicyVersionId)</c> when a single target has
///     multiple bindings to the same version: <c>Mandatory</c> wins over
///     <c>Recommended</c>; ties go to the earliest <c>CreatedAt</c>.</item>
/// </list>
/// <para>
/// Exact-match mode only in this story — no hierarchy walk. P4
/// (rivoli-ai/andy-policies#4) extends the resolver with stricter-tightens-only
/// hierarchy semantics behind a <c>?mode=hierarchy</c> flag.
/// </para>
/// </summary>
public interface IBindingResolver
{
    Task<ResolveBindingsResponse> ResolveExactAsync(
        BindingTargetType targetType,
        string targetRef,
        CancellationToken ct = default);
}
