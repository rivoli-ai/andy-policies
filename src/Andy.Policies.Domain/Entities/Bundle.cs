// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Domain.Entities;

/// <summary>
/// Immutable point-in-time snapshot of the catalog (Policy +
/// PolicyVersion + Binding + ScopeNode + Override). P8.1
/// (story rivoli-ai/andy-policies#81) introduces the entity; the
/// snapshot builder lands in P8.2 and the resolution endpoints in P8.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reproducibility contract.</b> Consumers pin a bundle id via the
/// P8.4 settings gate <c>andy.policies.bundleVersionPinning</c> and
/// receive the materialized snapshot regardless of subsequent
/// publishes / soft-deletes / override transitions. <see cref="SnapshotJson"/>
/// is the entire frozen graph; resolves never consult live state.
/// </para>
/// <para>
/// <b>Tamper-evidence.</b> <see cref="SnapshotHash"/> is
/// <c>SHA-256(canonicalJson(snapshot))</c> hex-encoded; the same
/// bytes emitted by canonical serialization sit in
/// <see cref="SnapshotJson"/>, so verifiers (P8.7) can recompute the
/// hash and detect storage-layer tamper. The hash is also the payload
/// of the <c>bundle.create</c> audit event so the P6 chain
/// cross-references the snapshot.
/// </para>
/// <para>
/// <b>Immutability.</b> Once inserted, only <see cref="State"/>,
/// <see cref="DeletedAt"/>, and <see cref="DeletedBySubjectId"/> may
/// change (the soft-delete flip in P8.5).
/// <c>AppDbContext.SaveChangesAsync</c> rejects every other modified
/// scalar property on a tracked active bundle.
/// </para>
/// <para>
/// <b>No hard delete.</b> P8 explicitly forbids removing rows so the
/// audit chain's <c>bundle.create</c> reference remains resolvable.
/// </para>
/// </remarks>
public class Bundle
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Slug-shaped identifier (<c>[a-z0-9-]</c>, ≤64 chars). Unique among
    /// active bundles via a filtered unique index that both Postgres and
    /// SQLite (≥ 3.8) honour — soft-deleted bundles release the slug for
    /// reuse on both providers. Validation lives in the service, not the
    /// entity, mirroring <see cref="Policy.Name"/>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string CreatedBySubjectId { get; set; } = string.Empty;

    /// <summary>
    /// Full materialized snapshot — the canonical-JSON UTF-8 bytes used
    /// to compute <see cref="SnapshotHash"/>. Stored as <c>jsonb</c> on
    /// Postgres (TOAST-compressed, indexable) and <c>TEXT</c> on SQLite
    /// (no native jsonb; embedded mode is read-tolerant of small
    /// payloads). The shape is
    /// <see cref="ValueObjects.BundleSnapshot"/>.
    /// </summary>
    public string SnapshotJson { get; set; } = "{}";

    /// <summary>
    /// SHA-256 of <see cref="SnapshotJson"/> (hex-encoded, lower-case,
    /// 64 chars). Verifiers recompute and compare; a mismatch means
    /// the row was edited outside the immutability guard. Persisted
    /// as <c>CHAR(64)</c> on Postgres and <c>TEXT</c> on SQLite.
    /// </summary>
    public string SnapshotHash { get; set; } = string.Empty;

    public BundleState State { get; set; } = BundleState.Active;

    /// <summary>Soft-delete tombstone. Null while the bundle is active.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public string? DeletedBySubjectId { get; set; }
}
