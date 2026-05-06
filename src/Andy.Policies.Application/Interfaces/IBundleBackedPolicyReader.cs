// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Queries;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Snapshot-backed read shim for the P1.5 policy endpoints (P8.4,
/// rivoli-ai/andy-policies#84). When a caller pins
/// <c>?bundleId=&lt;guid&gt;</c> and pinning is required, the API
/// dispatches here instead of <see cref="IPolicyService"/>; the
/// answer comes from the bundle's frozen <c>SnapshotJson</c> rather
/// than live tables.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape compatibility, not data parity.</b> The returned
/// <see cref="PolicyDto"/> / <see cref="PolicyVersionDto"/> use the
/// existing wire shapes so consumers don't fork their schemas, but
/// fields that the snapshot does not carry — <c>CreatedAt</c>,
/// <c>CreatedBySubjectId</c>, <c>ProposerSubjectId</c>,
/// <c>VersionCount</c>, <c>ActiveVersionId</c> on
/// <see cref="PolicyDto"/> — are filled with snapshot-derived
/// surrogates: <c>CapturedAt</c> for timestamps, <c>"snapshot"</c>
/// for subject ids, the active version's <c>VersionId</c> for the
/// active link. A consumer that needs the original creator metadata
/// must read live state.
/// </para>
/// <para>
/// <b>Version history.</b> A bundle snapshot only carries the
/// <see cref="Domain.Enums.LifecycleState.Active"/> version per
/// policy. The bundle-backed <c>ListVersions</c> therefore returns
/// at most one entry per policy (the pinned active), not the full
/// historical ladder.
/// </para>
/// <para>
/// <b>Filter applicability.</b> <c>ListPoliciesAsync</c> applies the
/// same filters as <see cref="IPolicyService.ListPoliciesAsync"/>
/// (name prefix, scope membership, enforcement, severity) against
/// the snapshot rows. Skip / take pagination is honoured. Filters
/// that don't apply to a snapshot (e.g. ones predicated on live
/// state) are no-ops.
/// </para>
/// </remarks>
public interface IBundleBackedPolicyReader
{
    /// <summary>List policies in the snapshot, optionally filtered.
    /// Returns <c>null</c> when the bundle is missing / deleted.</summary>
    Task<IReadOnlyList<PolicyDto>?> ListPoliciesAsync(
        Guid bundleId, ListPoliciesQuery query, CancellationToken ct = default);

    Task<PolicyDto?> GetPolicyAsync(Guid bundleId, Guid policyId, CancellationToken ct = default);

    Task<PolicyDto?> GetPolicyByNameAsync(Guid bundleId, string name, CancellationToken ct = default);

    Task<IReadOnlyList<PolicyVersionDto>?> ListVersionsAsync(
        Guid bundleId, Guid policyId, CancellationToken ct = default);

    Task<PolicyVersionDto?> GetVersionAsync(
        Guid bundleId, Guid policyId, Guid versionId, CancellationToken ct = default);

    Task<PolicyVersionDto?> GetActiveVersionAsync(
        Guid bundleId, Guid policyId, CancellationToken ct = default);
}
