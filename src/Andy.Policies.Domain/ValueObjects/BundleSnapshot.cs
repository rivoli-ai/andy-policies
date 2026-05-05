// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.ValueObjects;

/// <summary>
/// Materialized point-in-time view of the catalog persisted as
/// <c>Bundle.SnapshotJson</c>. P8.1 (rivoli-ai/andy-policies#81)
/// defines the shape; P8.2 builds it under a serializable transaction
/// and P8.3 reads it back at <c>/resolve</c> time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a Domain value object?</b> The snapshot is the authoritative
/// frozen view that defines the reproducibility contract — it is the
/// canonical materialized form, not a transport DTO. Surfaces (REST,
/// gRPC, MCP) project this into their own envelopes; the value
/// object stays surface-agnostic.
/// </para>
/// <para>
/// <b>Determinism.</b> Lists are emitted in stable order (P8.2 sorts
/// by primary id ascending) so canonical serialization produces
/// byte-identical output for logically equal snapshots. The hash
/// invariant <c>SHA-256(canonicalJson(snapshot)) == Bundle.SnapshotHash</c>
/// depends on this — a future contributor adding a new collection
/// must extend the sort to cover it or the hash diverges
/// non-deterministically across runs.
/// </para>
/// </remarks>
public sealed record BundleSnapshot(
    string SchemaVersion,
    DateTimeOffset CapturedAt,
    string AuditTailHash,
    IReadOnlyList<BundlePolicyEntry> Policies,
    IReadOnlyList<BundleBindingEntry> Bindings,
    IReadOnlyList<BundleOverrideEntry> Overrides,
    IReadOnlyList<BundleScopeEntry> Scopes);

/// <summary>One row per active <c>PolicyVersion</c> at snapshot time.</summary>
public sealed record BundlePolicyEntry(
    Guid PolicyId,
    string Name,
    Guid PolicyVersionId,
    int Version,
    string Enforcement,
    string Severity,
    IReadOnlyList<string> Scopes,
    string RulesJson,
    string Summary);

/// <summary>One row per live binding (DeletedAt IS NULL) at snapshot time.</summary>
public sealed record BundleBindingEntry(
    Guid BindingId,
    Guid PolicyVersionId,
    string TargetType,
    string TargetRef,
    string BindStrength);

/// <summary>One row per Approved override that has not yet expired.</summary>
public sealed record BundleOverrideEntry(
    Guid OverrideId,
    Guid PolicyVersionId,
    string ScopeKind,
    string ScopeRef,
    string Effect,
    Guid? ReplacementPolicyVersionId,
    DateTimeOffset ExpiresAt);

/// <summary>One row per scope node at snapshot time.</summary>
public sealed record BundleScopeEntry(
    Guid ScopeNodeId,
    Guid? ParentId,
    string Type,
    string Ref,
    string DisplayName);
