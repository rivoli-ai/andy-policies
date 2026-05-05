// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.ValueObjects;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Reads the live catalog and materialises a
/// <see cref="BundleSnapshot"/>. P8.2 (story rivoli-ai/andy-policies#82)
/// splits this off <see cref="IBundleService"/> so the read-materialise
/// step can be unit-tested independently of the txn-orchestration +
/// audit-append step.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transactional ownership.</b> The builder issues queries against
/// the shared <c>AppDbContext</c> and assumes the caller has already
/// opened a serializable transaction. The "frozen catalog" guarantee
/// is the caller's responsibility — see <see cref="IBundleService.CreateAsync"/>.
/// </para>
/// <para>
/// <b>Determinism.</b> Collections are emitted in stable order
/// (policies by <c>(PolicyId, Version)</c>; bindings/overrides/scopes
/// by <c>Id</c>) so canonical-JSON serialisation produces byte-
/// identical output for two builds against the same catalog state.
/// The hash invariant <c>SHA-256(canonicalJson(snapshot)) ==
/// Bundle.SnapshotHash</c> depends on this.
/// </para>
/// </remarks>
public interface IBundleSnapshotBuilder
{
    /// <summary>
    /// Build a snapshot of the catalog as visible inside the caller's
    /// transaction. <paramref name="capturedAt"/> is the instant the
    /// caller wants stamped into <see cref="BundleSnapshot.CapturedAt"/>;
    /// it is also the cutoff for "non-expired" override rows.
    /// </summary>
    Task<BundleSnapshot> BuildAsync(DateTimeOffset capturedAt, CancellationToken ct = default);
}
