// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Hierarchy-aware resolution: walks the scope chain from root to
/// leaf, gathers every binding that targets a node in the chain (or
/// the chain's own external <c>Ref</c> via the bridge to P3 non-scope
/// bindings), and folds them with stricter-tightens-only semantics
/// (P4.3, story rivoli-ai/andy-policies#30).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="IBindingResolver"/> from P3.4: that
/// service does an exact-match lookup at a single target;
/// <see cref="IBindingResolutionService"/> walks the chain and merges.
/// </para>
/// <para>
/// <b>Tighten-only fold rules</b>:
/// <list type="bullet">
///   <item>For each <c>PolicyId</c> seen in the chain, a Mandatory
///     binding anywhere wins over any Recommended binding for the
///     same policy (descendant Recommended cannot downgrade an
///     ancestor Mandatory).</item>
///   <item>Among bindings of the strictest strength present, the
///     deepest binding wins (most-specific-wins). Tied depth +
///     strength: earliest <c>CreatedAt</c> wins.</item>
///   <item>Result is sorted Mandatory-first, then by
///     <c>PolicyKey</c> alphabetically.</item>
/// </list>
/// </para>
/// <para>
/// Retired versions are <b>not</b> filtered here — what's bound is
/// what's returned. Consumers (Conductor's ActionBus, andy-tasks
/// per-task gates) decide how to handle deprecation. This differs
/// deliberately from <see cref="IBindingResolver.ResolveExactAsync"/>,
/// which filters Retired because it's a single-target view; chain
/// resolution surfaces the entire policy story.
/// </para>
/// </remarks>
public interface IBindingResolutionService
{
    /// <summary>
    /// Resolve the effective policy set for a known scope node.
    /// Throws <c>NotFoundException</c> when the scope node does not
    /// exist.
    /// </summary>
    Task<EffectivePolicySetDto> ResolveForScopeAsync(
        Guid scopeNodeId,
        CancellationToken ct = default);

    /// <summary>
    /// Resolve the effective policy set for a foreign target. If
    /// <paramref name="targetType"/>/<paramref name="targetRef"/>
    /// maps to a known <see cref="Domain.Entities.ScopeNode"/>, the
    /// resolver walks from that node. Otherwise it degrades to P3
    /// exact-match semantics — returns whatever bindings target the
    /// pair directly, with <c>SourceScopeNodeId</c> = null on the
    /// envelope.
    /// </summary>
    Task<EffectivePolicySetDto> ResolveForTargetAsync(
        BindingTargetType targetType,
        string targetRef,
        CancellationToken ct = default);
}
