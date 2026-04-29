// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Domain.Entities;

/// <summary>
/// Hierarchical scope node — a single position in the
/// <c>Org → Tenant → Team → Repo → Template → Run</c> tree (P4.1,
/// story rivoli-ai/andy-policies#28). Subsequent stories add CRUD
/// (P4.2), tighten-only resolution (P4.3 / P4.4), surfaces (P4.5–P4.6),
/// and the design-doc + ADR closeout (P4.7 / P4.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>MaterializedPath</b>. The service layer maintains a forward-slash-
/// separated path of ancestor ids ending in self
/// (<c>"/{rootId}/.../{selfId}"</c>) so descendant lookups can use
/// <c>LIKE '/root/%'</c> on both Postgres and SQLite without falling
/// back to recursive CTEs. <see cref="Depth"/> mirrors the enum
/// ordinal and is enforced equal to <see cref="Type"/> by the service
/// layer.
/// </para>
/// <para>
/// <b>Re-parenting is out of scope for Epic P4.</b> Once a node is
/// inserted with a given <see cref="ParentId"/>, the parent linkage
/// is treated as immutable by the service layer (P4.2 rejects
/// <see cref="ParentId"/> changes); this lets us avoid the cycle-
/// prevention machinery a mutable hierarchy would require.
/// </para>
/// </remarks>
public sealed class ScopeNode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Null for a root <see cref="ScopeType.Org"/> node. Otherwise
    /// references the immediate parent node id.
    /// </summary>
    public Guid? ParentId { get; set; }

    public ScopeType Type { get; set; }

    /// <summary>
    /// Opaque foreign reference (e.g. <c>"repo:rivoli-ai/conductor"</c>,
    /// <c>"tenant:{guid}"</c>). Capped at 512 chars; service-layer
    /// validation in P4.2 enforces non-empty + ≤512.
    /// </summary>
    public string Ref { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Slash-separated path of ancestor ids ending in self. Indexed
    /// for descendant lookup via <c>LIKE</c>; managed by the service
    /// layer (P4.2) on insert.
    /// </summary>
    public string MaterializedPath { get; set; } = string.Empty;

    /// <summary>0 for a root Org; <c>parent.Depth + 1</c> otherwise.
    /// Equals <c>(int)Type</c> by service-layer invariant.</summary>
    public int Depth { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ScopeNode? Parent { get; set; }

    public ICollection<ScopeNode> Children { get; set; } = new List<ScopeNode>();
}
