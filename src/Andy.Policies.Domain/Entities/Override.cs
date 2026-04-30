// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Domain.Entities;

/// <summary>
/// Per-principal or per-cohort policy override (P5.1, story
/// rivoli-ai/andy-policies#49). The escape hatch from
/// stricter-tightens-only resolution: an approved override can
/// either exempt a principal/cohort from a Mandatory binding or
/// replace it with a different <see cref="PolicyVersion"/>, with
/// approver, rationale, and expiry recorded for audit (P6).
/// </summary>
/// <remarks>
/// <para>
/// <b>State machine</b> (P5.2 enforces transitions):
/// <c>Proposed → Approved → (Revoked | Expired)</c>. The reaper
/// (P5.3) is the only path into <c>Expired</c>; explicit revocation
/// is operator-driven and stamps a non-null
/// <see cref="RevocationReason"/>.
/// </para>
/// <para>
/// <b>Effect invariants</b> (CHECK constraint at the DB layer):
/// <see cref="OverrideEffect.Exempt"/> rows have null
/// <see cref="ReplacementPolicyVersionId"/>;
/// <see cref="OverrideEffect.Replace"/> rows carry a non-null one
/// pointing at the alternate <c>PolicyVersion</c>.
/// </para>
/// <para>
/// <b>Cohort membership is not stored here.</b> <see cref="ScopeRef"/>
/// is an opaque consumer-defined string; this service never expands
/// the cohort or resolves principal identities (per Epic P5
/// Non-goals).
/// </para>
/// </remarks>
public class Override
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PolicyVersionId { get; set; }

    public PolicyVersion PolicyVersion { get; set; } = default!;

    public OverrideScopeKind ScopeKind { get; set; }

    /// <summary>
    /// Opaque consumer-defined reference identifying the principal
    /// or cohort (e.g. <c>user:42</c>, <c>cohort:beta-testers</c>).
    /// Capped at 256 chars; service-layer validation in P5.2
    /// enforces non-empty + ≤256.
    /// </summary>
    public string ScopeRef { get; set; } = string.Empty;

    public OverrideEffect Effect { get; set; }

    /// <summary>
    /// Non-null when <see cref="Effect"/> is
    /// <see cref="OverrideEffect.Replace"/>; null when
    /// <see cref="OverrideEffect.Exempt"/>. The CHECK constraint
    /// <c>ck_overrides_effect_replacement</c> enforces this on
    /// every insert/update.
    /// </summary>
    public Guid? ReplacementPolicyVersionId { get; set; }

    public PolicyVersion? ReplacementPolicyVersion { get; set; }

    public string ProposerSubjectId { get; set; } = string.Empty;

    /// <summary>Non-null once the override transitions to
    /// <see cref="OverrideState.Approved"/>. P5.2 enforces
    /// approver != proposer.</summary>
    public string? ApproverSubjectId { get; set; }

    public OverrideState State { get; set; } = OverrideState.Proposed;

    public DateTimeOffset ProposedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string Rationale { get; set; } = string.Empty;

    /// <summary>Non-null only after explicit revocation;
    /// reaper-driven expiry leaves this null.</summary>
    public string? RevocationReason { get; set; }

    /// <summary>
    /// Optimistic-concurrency token. Bumped manually in
    /// <c>AppDbContext.SaveChangesAsync</c> on every modification,
    /// matching the cross-provider pattern used by
    /// <see cref="PolicyVersion"/>.
    /// </summary>
    public uint Revision { get; set; }
}
