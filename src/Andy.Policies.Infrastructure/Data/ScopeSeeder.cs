// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Data;

/// <summary>
/// Boot-time seeder for the canonical root <see cref="ScopeNode"/>
/// (P4.1, story rivoli-ai/andy-policies#28). The root <see cref="ScopeType.Org"/>
/// node anchors the hierarchy walks introduced in P4.3 — without it,
/// every scope-aware endpoint would 404 on a fresh database. P4.2's
/// CRUD endpoints attach children under this root.
/// </summary>
/// <remarks>
/// Idempotency is by-presence: if any node with <c>ParentId IS NULL</c>
/// already exists we short-circuit. That preserves operator edits
/// across restarts and lets the seeder run from <c>Program.cs</c> on
/// every boot.
/// </remarks>
public static class ScopeSeeder
{
    /// <summary>
    /// Stable id for the seeded root Org. Tests and downstream
    /// integration code that need to reference "the root" without a
    /// query lookup can hard-code this value.
    /// </summary>
    public static readonly Guid RootOrgId = new("00000000-0000-0000-0000-0000000040a1");

    /// <summary>Canonical <see cref="ScopeNode.Ref"/> for the seeded root.</summary>
    public const string RootOrgRef = "org:root";

    public static async Task SeedRootScopeAsync(AppDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var anyRoot = await db.ScopeNodes
            .AsNoTracking()
            .AnyAsync(s => s.ParentId == null, ct)
            .ConfigureAwait(false);
        if (anyRoot)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var root = new ScopeNode
        {
            Id = RootOrgId,
            ParentId = null,
            Type = ScopeType.Org,
            Ref = RootOrgRef,
            DisplayName = "Root Organisation",
            MaterializedPath = $"/{RootOrgId}",
            Depth = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ScopeNodes.Add(root);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
