// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Entities;

/// <summary>
/// Stable identity for a governance policy. Holds name uniqueness and creation metadata only;
/// all content and lifecycle state live on <see cref="PolicyVersion"/>.
/// </summary>
/// <remarks>
/// Aggregate shape pinned by ADR 0001 (docs/adr/0001-policy-versioning.md):
/// a <see cref="Policy"/> never carries version-dependent fields. Rename/description edits
/// are OK on the stable row; edits to rules / enforcement / severity / scopes happen on
/// a <see cref="PolicyVersion"/> in <see cref="Enums.LifecycleState.Draft"/>.
/// </remarks>
public class Policy
{
    public Guid Id { get; set; }

    /// <summary>
    /// Unique slug. Validation regex <c>^[a-z0-9][a-z0-9-]{0,62}$</c> is enforced at the service layer (P1.4);
    /// DB uniqueness is enforced via an index configured in <see cref="Infrastructure.Data.AppDbContext"/>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string CreatedBySubjectId { get; set; } = string.Empty;

    public ICollection<PolicyVersion> Versions { get; set; } = new List<PolicyVersion>();
}
