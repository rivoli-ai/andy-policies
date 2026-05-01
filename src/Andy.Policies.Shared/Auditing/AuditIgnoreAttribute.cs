// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Shared.Auditing;

/// <summary>
/// Excludes a DTO property from the audit diff (P6.3, story
/// rivoli-ai/andy-policies#43). The
/// <c>JsonPatchDiffGenerator</c> never emits an op (add /
/// replace / remove) for an ignored property, even when the
/// property's value differs between snapshots.
/// </summary>
/// <remarks>
/// Use for properties that are computed, denormalised, or
/// otherwise not part of the canonical state being audited.
/// Examples: <c>ModifiedAt</c> timestamps that the persistence
/// layer maintains, <c>RowVersion</c> concurrency tokens, cached
/// counts derived from related rows. Do <i>not</i> use this to
/// hide sensitive fields — that's <c>[AuditRedact]</c>'s job.
/// Hiding via <c>[AuditIgnore]</c> means the field changes
/// silently in the audit chain, which defeats the tamper-evident
/// invariant.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class AuditIgnoreAttribute : Attribute
{
}
