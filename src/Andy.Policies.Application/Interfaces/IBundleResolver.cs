// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Domain.ValueObjects;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Resolution-shaped read against a frozen <c>Bundle</c> snapshot
/// (P8.3, story rivoli-ai/andy-policies#83). Mirrors
/// <see cref="IBindingResolver"/>'s exact-match contract — same
/// dedup rule, same ordering — but reads from the bundle's
/// pre-materialised <c>SnapshotJson</c> instead of live tables, so
/// answers are reproducible across catalog mutations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Override application is intentionally out of scope</b> for
/// P8.3. The live <see cref="IBindingResolver"/> doesn't apply
/// override effects either — that lives in P5's flow. Bundle-time
/// override semantics will be pinned by ADR 0008 (P8.8) and wired
/// in a follow-up.
/// </para>
/// </remarks>
public interface IBundleResolver
{
    /// <summary>
    /// Resolve bindings for an exact <c>(targetType, targetRef)</c>
    /// pair against the bundle's snapshot. Returns <c>null</c> when
    /// the bundle does not exist or is soft-deleted; returns an
    /// empty <see cref="BundleResolveResult.Bindings"/> when the
    /// bundle exists but has no bindings for the target.
    /// </summary>
    Task<BundleResolveResult?> ResolveAsync(
        Guid bundleId,
        BindingTargetType targetType,
        string targetRef,
        CancellationToken ct = default);

    /// <summary>
    /// Look up a single pinned policy in a bundle by its
    /// <c>PolicyId</c>. Returns <c>null</c> when the bundle is
    /// missing/deleted or the policy is not in the snapshot.
    /// </summary>
    Task<BundlePinnedPolicyDto?> GetPinnedPolicyAsync(
        Guid bundleId,
        Guid policyId,
        CancellationToken ct = default);

    /// <summary>
    /// Return the parsed <see cref="BundleSnapshot"/> for the given
    /// bundle, sharing the resolver's <c>(bundleId, snapshotHash)</c>
    /// cache. P8.4 (#84) added this so the policy / effective-policy
    /// snapshot-backed readers don't each re-parse the same JSON;
    /// also yields the bundle name and hash for callers that need
    /// to stamp envelopes with snapshot coordinates. Returns
    /// <c>null</c> when the bundle is missing / soft-deleted.
    /// </summary>
    Task<BundleSnapshotView?> GetSnapshotAsync(Guid bundleId, CancellationToken ct = default);

    /// <summary>
    /// Snapshot-backed equivalent of
    /// <see cref="IBindingResolutionService.ResolveForScopeAsync"/>
    /// (P8.4, #84). Walks the bundle's scope tree from the target
    /// node up to its root and folds matching bindings with
    /// stricter-tightens-only semantics. Returns <c>null</c> when
    /// the bundle is missing / soft-deleted; returns an
    /// <see cref="EffectivePolicySetDto"/> with empty
    /// <c>Policies</c> when the bundle exists but the scope node
    /// is absent from it.
    /// </summary>
    /// <remarks>
    /// <b>Scope of dispatch.</b> This method matches only
    /// <c>TargetType=ScopeNode</c> bindings keyed by
    /// <c>scope:{nodeId}</c>. The bridge logic from
    /// <c>BindingResolutionService</c> (which also matches
    /// <c>Repo</c> / <c>Tenant</c> / <c>Org</c> / <c>Template</c>
    /// targets against the chain's external refs) is deferred to
    /// a follow-up; consumers using bridge-typed bindings should
    /// keep <c>bundleVersionPinning=false</c> until then.
    /// </remarks>
    Task<EffectivePolicySetDto?> ResolveEffectiveForScopeAsync(
        Guid bundleId, Guid scopeNodeId, CancellationToken ct = default);

    /// <summary>
    /// P9 follow-up #204 (2026-05-07): denormalised contents tree for the
    /// frozen-tree view in the bundle detail page. Projects the parsed
    /// snapshot into a policies-with-nested-bindings shape so the UI can
    /// render <c>FrozenPolicyTreeComponent</c> in a single call instead
    /// of fanning out to <c>GET /policies/{id}</c> per row. Returns
    /// <c>null</c> when the bundle is missing / soft-deleted.
    /// </summary>
    /// <remarks>
    /// Bindings are grouped by their <c>PolicyVersionId</c>; an empty
    /// nested list is allowed (a policy may be in the bundle without
    /// any bindings, e.g. the seed policy of a fresh bundle).
    /// Wire casing matches the live surfaces — <c>Enforcement</c>
    /// uppercase, <c>Severity</c> lowercase, per ADR 0001 §6.
    /// <c>RulesJson</c> is intentionally omitted — fetch the per-policy
    /// detail at <c>GET /api/bundles/{id}/policies/{policyId}</c> when
    /// the rule body is needed.
    /// </remarks>
    Task<BundleContentsDto?> GetContentsAsync(Guid bundleId, CancellationToken ct = default);
}

/// <summary>
/// Denormalised contents view of a frozen bundle (P9 follow-up #204,
/// 2026-05-07). Consumers (the bundle detail page) render this as a
/// tree: policies grouped by name, each expandable to its bindings,
/// plus a flat list of overrides. <c>SnapshotHash</c> is the same
/// strong validator the resolution endpoints emit so callers can
/// share an HTTP cache between this endpoint and <c>/resolve</c>.
/// </summary>
public sealed record BundleContentsDto(
    Guid BundleId,
    string BundleName,
    string SnapshotHash,
    DateTimeOffset CapturedAt,
    IReadOnlyList<BundleContentsPolicyDto> Policies,
    IReadOnlyList<BundleContentsOverrideDto> Overrides);

public sealed record BundleContentsPolicyDto(
    Guid PolicyId,
    string Name,
    Guid PolicyVersionId,
    int Version,
    string Enforcement,
    string Severity,
    IReadOnlyList<string> Scopes,
    string Summary,
    IReadOnlyList<BundleContentsBindingDto> Bindings);

public sealed record BundleContentsBindingDto(
    Guid BindingId,
    string TargetType,
    string TargetRef,
    string BindStrength);

public sealed record BundleContentsOverrideDto(
    Guid OverrideId,
    Guid PolicyVersionId,
    string ScopeKind,
    string ScopeRef,
    string Effect,
    Guid? ReplacementPolicyVersionId,
    DateTimeOffset ExpiresAt);

/// <summary>
/// View of a bundle row + its parsed snapshot (P8.4, #84). Carries
/// the identity coordinates so callers projecting the snapshot into
/// surface DTOs can stamp the bundle id, name, and hash.
/// </summary>
public sealed record BundleSnapshotView(
    Guid BundleId,
    string BundleName,
    string SnapshotHash,
    BundleSnapshot Snapshot);

/// <summary>
/// Envelope for <see cref="IBundleResolver.ResolveAsync"/>. Carries
/// the bundle identity + snapshot coordinate (<see cref="SnapshotHash"/>,
/// <see cref="CapturedAt"/>) so callers caching the response have
/// everything they need to validate it against a re-fetched bundle.
/// </summary>
public sealed record BundleResolveResult(
    Guid BundleId,
    string BundleName,
    string SnapshotHash,
    DateTimeOffset CapturedAt,
    BindingTargetType TargetType,
    string TargetRef,
    IReadOnlyList<BundleResolvedBindingDto> Bindings,
    int Count);

/// <summary>
/// Per-binding row in <see cref="BundleResolveResult.Bindings"/>.
/// Mirrors <see cref="Andy.Policies.Application.Dtos.ResolvedBindingDto"/>
/// from P3.4: same wire-format casing, same fields. The only
/// difference is the data source (frozen snapshot vs. live tables).
/// </summary>
public sealed record BundleResolvedBindingDto(
    Guid BindingId,
    Guid PolicyId,
    string PolicyName,
    Guid PolicyVersionId,
    int VersionNumber,
    string Enforcement,
    string Severity,
    IReadOnlyList<string> Scopes,
    BindStrength BindStrength);

/// <summary>
/// Single-policy projection from a bundle snapshot. Returned by
/// <see cref="IBundleResolver.GetPinnedPolicyAsync"/>; carries the
/// snapshot coordinates so callers can stamp ETags / cache keys.
/// </summary>
public sealed record BundlePinnedPolicyDto(
    Guid BundleId,
    string BundleName,
    string SnapshotHash,
    DateTimeOffset CapturedAt,
    Guid PolicyId,
    string PolicyName,
    Guid PolicyVersionId,
    int VersionNumber,
    string Enforcement,
    string Severity,
    IReadOnlyList<string> Scopes,
    string RulesJson,
    string Summary);
