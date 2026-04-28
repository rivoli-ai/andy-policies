// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Domain.Entities;

/// <summary>
/// Metadata link between an immutable <see cref="PolicyVersion"/> and a
/// foreign target (template, repo, scope node, tenant, org). P3.1 (story
/// rivoli-ai/andy-policies#19) introduces the entity; mutation services
/// land in P3.2 and the four parity surfaces (REST/MCP/gRPC/CLI) follow.
/// </summary>
/// <remarks>
/// <para>
/// Bindings are <b>metadata only</b>. <see cref="TargetRef"/> is an opaque
/// foreign-system reference handed to us by a consumer (andy-issues,
/// andy-tasks, etc.); andy-policies never resolves it against the foreign
/// system. Cross-service consistency of the target is the consumer's
/// contract — see Epic P3 (rivoli-ai/andy-policies#3) Non-goals.
/// </para>
/// <para>
/// <b>Canonical TargetRef shapes</b> (validated in P3.2 BindingService):
/// <list type="bullet">
///   <item><c>template:{guid}</c></item>
///   <item><c>repo:{org}/{name}</c></item>
///   <item><c>scope:{guid}</c> — the <c>ScopeNode.Id</c>, not the
///     materialised path; P4.2 resolves path → id when callers want
///     path-based lookup.</item>
///   <item><c>tenant:{guid}</c></item>
///   <item><c>org:{guid}</c></item>
/// </list>
/// Keeping a single canonical shape per <see cref="TargetType"/> lets P4's
/// hierarchy walk and P8's bundle resolve share the same join key without
/// string parsing heuristics.
/// </para>
/// <para>
/// <b>Soft-delete</b>: P3.2 sets <see cref="DeletedAt"/> rather than
/// hard-deleting rows so P6's audit chain has an append-only history of
/// every mutation. Read endpoints (P3.3, P3.4, P4) filter
/// <c>DeletedAt IS NULL</c>.
/// </para>
/// </remarks>
public class Binding
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PolicyVersionId { get; set; }

    public PolicyVersion? PolicyVersion { get; set; }

    public BindingTargetType TargetType { get; set; }

    /// <summary>
    /// Opaque foreign reference matching one of the canonical shapes per
    /// <see cref="TargetType"/> (see remarks). Capped at 512 chars; longer
    /// values fail validation in P3.2.
    /// </summary>
    public string TargetRef { get; set; } = string.Empty;

    public BindStrength BindStrength { get; set; } = BindStrength.Recommended;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string CreatedBySubjectId { get; set; } = string.Empty;

    /// <summary>
    /// Soft-delete tombstone. Null while the binding is active; set by P3.2
    /// <c>BindingService.DeleteAsync</c>. Indexed via
    /// <c>ix_bindings_deleted_at</c> for fast active-only filtering.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public string? DeletedBySubjectId { get; set; }
}
