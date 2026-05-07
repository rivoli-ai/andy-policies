// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Application-layer service for <c>Bundle</c> mutation and query (P8.2,
/// story rivoli-ai/andy-policies#82). REST (P8.3), MCP (P8.5), gRPC +
/// CLI (P8.6) all delegate here so the snapshot-build / hash /
/// audit-append discipline is uniform across surfaces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reproducibility contract.</b> <see cref="CreateAsync"/> opens a
/// serializable transaction and atomically (a) reads the live catalog,
/// (b) materialises a <see cref="Domain.ValueObjects.BundleSnapshot"/>,
/// (c) hashes it into a 64-char hex SHA-256, (d) inserts the
/// <see cref="Domain.Entities.Bundle"/> row, (e) appends a
/// <c>bundle.create</c> event to the audit chain. A concurrent
/// publish that commits between the read and the bundle insert
/// must NOT leak into the bundle — the serializable level is
/// load-bearing.
/// </para>
/// </remarks>
public interface IBundleService
{
    /// <summary>
    /// Snapshot the live catalog and persist it as an immutable
    /// <see cref="Domain.Entities.Bundle"/>.
    /// </summary>
    /// <exception cref="Exceptions.ValidationException">
    /// Slug-shape violations (<see cref="CreateBundleRequest.Name"/> must
    /// match <c>^[a-z0-9][a-z0-9-]{0,62}$</c>) or empty rationale.
    /// </exception>
    /// <exception cref="Exceptions.ConflictException">
    /// An active bundle already owns the requested name.
    /// </exception>
    Task<BundleDto> CreateAsync(CreateBundleRequest request, string actorSubjectId, CancellationToken ct = default);

    Task<BundleDto?> GetAsync(Guid bundleId, CancellationToken ct = default);

    Task<IReadOnlyList<BundleDto>> ListAsync(ListBundlesFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Flip <see cref="Domain.Entities.Bundle.State"/> to
    /// <see cref="Domain.Enums.BundleState.Deleted"/> and stamp the
    /// tombstone trio. Returns <c>false</c> if the bundle does not
    /// exist or was already tombstoned (idempotent caller contract).
    /// Appends a <c>bundle.delete</c> audit event when the flip
    /// actually happens.
    /// </summary>
    Task<bool> SoftDeleteAsync(Guid bundleId, string actorSubjectId, string rationale, CancellationToken ct = default);
}

/// <summary>Inputs to <see cref="IBundleService.CreateAsync"/>.</summary>
/// <param name="Name">Slug shape: <c>^[a-z0-9][a-z0-9-]{0,62}$</c>.
/// Active bundles are unique by name; soft-deleted bundles release
/// the slug for reuse.</param>
/// <param name="Description">Optional human-readable summary.</param>
/// <param name="Rationale">Required non-empty rationale recorded
/// against the audit event.</param>
/// <param name="IncludeOverrides">When true (P9 follow-up #205,
/// 2026-05-07), the snapshot also captures the current set of
/// Approved, non-expired overrides. Default <c>true</c> preserves the
/// pre-#205 behaviour where overrides were always included; pass
/// <c>false</c> for compliance / immutability bundles where overrides
/// are intentionally excluded.</param>
public sealed record CreateBundleRequest(
    string Name,
    string? Description,
    string Rationale,
    bool IncludeOverrides = true);

/// <summary>Filter for <see cref="IBundleService.ListAsync"/>.</summary>
public sealed record ListBundlesFilter(
    bool IncludeDeleted = false,
    int Skip = 0,
    int Take = 50);

/// <summary>
/// Wire shape returned by <see cref="IBundleService"/>. Excludes the
/// (potentially large) <c>SnapshotJson</c> payload — surfaces fetch
/// that separately on demand via <c>BundlesController</c> in P8.3.
/// </summary>
public sealed record BundleDto(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    string CreatedBySubjectId,
    string SnapshotHash,
    string State,
    DateTimeOffset? DeletedAt,
    string? DeletedBySubjectId);
