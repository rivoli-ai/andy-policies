// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Enums;

/// <summary>
/// Lifecycle state of a <see cref="Andy.Policies.Domain.Entities.Bundle"/>.
/// P8.1 (story rivoli-ai/andy-policies#81) introduces only two states —
/// <see cref="Active"/> and <see cref="Deleted"/> — because bundles are
/// immutable snapshots: there is no draft / publish / wind-down ladder
/// like <see cref="LifecycleState"/>; a bundle is either live or
/// soft-tombstoned.
/// </summary>
/// <remarks>
/// <para>
/// <b>No hard delete.</b> The audit chain (Epic P6) appends a
/// <c>bundle.create</c> event referencing the bundle id; deleting the
/// row would dangle that reference. P8.5's delete operation flips
/// state to <see cref="Deleted"/> and stamps the
/// <c>DeletedAt</c> / <c>DeletedBySubjectId</c> tombstone columns —
/// the row remains discoverable to verifiers.
/// </para>
/// <para>
/// Persisted as a string column on both providers (mirrors the
/// <see cref="LifecycleState"/> + <see cref="OverrideState"/> precedent)
/// so partial unique indexes on bundle name can filter
/// <c>WHERE "State" = 'Active'</c> without an int-to-string cast.
/// </para>
/// </remarks>
public enum BundleState
{
    Active = 0,

    /// <summary>Soft-deleted. The row is preserved for audit-chain integrity.</summary>
    Deleted = 1,
}
