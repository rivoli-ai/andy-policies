// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Emits an RFC-6902 JSON Patch between two bundles' frozen
/// snapshots (P8.6, story rivoli-ai/andy-policies#86). Reuses the
/// canonical-JSON shape persisted in <c>Bundle.SnapshotJson</c> so
/// the patch is deterministic — running the diff twice on the same
/// pair yields byte-identical output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Compares two bundles in this catalog only. Diff
/// against live state, against bundles in another deployment, or
/// against external systems is out of scope per Epic P8 non-goals;
/// adding such capabilities would encourage consumers to use this
/// service as a drift detector, which is Conductor's job.
/// </para>
/// <para>
/// <b>Why RFC-6902 vs RFC-7396.</b> JSON Merge Patch (7396) cannot
/// express array element removals precisely; we need "removed
/// binding at index 42" semantics so an auditor can grep
/// <c>"path": "/bindings/42"</c> in the output. JSON Patch
/// (6902) preserves indexed paths and round-trips cleanly against
/// jsonb storage.
/// </para>
/// </remarks>
public interface IBundleDiffService
{
    /// <summary>
    /// Compute the patch from <paramref name="fromId"/> to
    /// <paramref name="toId"/>. Returns <c>null</c> when either
    /// bundle is missing or soft-deleted.
    /// </summary>
    Task<BundleDiffResult?> DiffAsync(Guid fromId, Guid toId, CancellationToken ct = default);
}

/// <summary>
/// Result envelope for <see cref="IBundleDiffService.DiffAsync"/>.
/// </summary>
public sealed record BundleDiffResult(
    Guid FromId,
    string FromSnapshotHash,
    Guid ToId,
    string ToSnapshotHash,
    string Rfc6902PatchJson,
    int OpCount);
