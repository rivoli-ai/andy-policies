// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Domain.Entities;

/// <summary>
/// Append-only version row for a <see cref="Policy"/>.
/// Mutable only while <see cref="State"/> is <see cref="LifecycleState.Draft"/>; once transitioned
/// (per ADR 0002), content fields are immutable and edits are blocked by the <c>SaveChangesAsync</c>
/// guard on <see cref="Infrastructure.Data.AppDbContext"/>.
/// </summary>
/// <remarks>
/// Aggregate shape pinned by ADR 0001 (docs/adr/0001-policy-versioning.md). Consumer references
/// always target <see cref="Id"/> (a <see cref="Guid"/>) — the <see cref="Version"/> int is a
/// human-readable label, not a cross-service identifier.
///
/// Dimension fields (<c>Enforcement</c>, <c>Severity</c>, <c>Scopes</c>) are added in P1.2.
/// Lifecycle-transition fields (<see cref="PublishedAt"/>, <see cref="PublishedBySubjectId"/>,
/// <see cref="SupersededByVersionId"/>) ship nullable in P1.1 per ADR 0001 §Aggregate sketch;
/// P2 populates them via transition endpoints.
/// </remarks>
public class PolicyVersion
{
    public Guid Id { get; set; }

    public Guid PolicyId { get; set; }

    public Policy Policy { get; set; } = default!;

    /// <summary>Monotonic, starts at 1. Unique per <see cref="PolicyId"/>. Gaps are disallowed.</summary>
    public int Version { get; set; }

    /// <summary>Default <see cref="LifecycleState.Draft"/>. Transitions governed by P2 (ADR 0002).</summary>
    public LifecycleState State { get; set; } = LifecycleState.Draft;

    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// RFC 2119 posture (see <see cref="EnforcementLevel"/>). Defaults to
    /// <see cref="EnforcementLevel.Should"/> per ADR 0001 §6. Consumers interpret; this
    /// service does not enforce.
    /// </summary>
    public EnforcementLevel Enforcement { get; set; } = EnforcementLevel.Should;

    /// <summary>
    /// Triage tier (see <see cref="Domain.Enums.Severity"/>). Defaults to
    /// <see cref="Domain.Enums.Severity.Moderate"/> per ADR 0001 §6.
    /// </summary>
    public Severity Severity { get; set; } = Severity.Moderate;

    /// <summary>
    /// Flat list of applicability scopes (e.g. <c>prod</c>, <c>repo:rivoli-ai/conductor</c>,
    /// <c>tool:write-branch</c>). The hierarchy + stricter-tightens-only resolution is
    /// Epic P4 (rivoli-ai/andy-policies#4); P1 stores a flat list so consumers have a
    /// non-null scope field from day 1. Service-layer validation (P1.4) enforces the
    /// canonical regex <c>^[a-z][a-z0-9:._-]{0,62}$</c>, rejects the reserved wildcard
    /// <c>*</c>, deduplicates, and sorts before persistence.
    /// </summary>
    public IList<string> Scopes { get; set; } = new List<string>();

    /// <summary>
    /// Opaque JSON blob carrying the allow/deny/flags DSL preserved from the superseded
    /// rivoli-ai/andy-rbac#17 (Epic V1). This service never interprets it — consumers
    /// (Conductor ActionBus, andy-tasks approval gates) own the schema. Byte-stable storage
    /// (RFC 8785 JCS per ADR 0006) lands at save-time in P1.4; for P1.1 this is a plain string.
    /// </summary>
    public string RulesJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string CreatedBySubjectId { get; set; } = string.Empty;

    /// <summary>
    /// The subject who proposed the publish for this version. Load-bearing for the
    /// author-cannot-self-approve invariant in ADR 0007 §Decision 4 — at publish time
    /// the actor must satisfy <c>actor != ProposerSubjectId</c>. Defaulted to
    /// <see cref="CreatedBySubjectId"/> on creation; may be re-assigned before publish.
    /// Never modified after publish.
    /// </summary>
    public string ProposerSubjectId { get; set; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; set; }

    public string? PublishedBySubjectId { get; set; }

    /// <summary>
    /// Author-driven \"this draft is ready for an approver to look at\" flag (#216).
    /// Set by <c>POST .../propose</c>; cleared by <c>POST .../reject</c> or
    /// (implicitly) when the version transitions out of Draft. Filtered on by
    /// the approver inbox query <c>GET /api/policies/pending-approval</c>.
    /// Distinct from <see cref="ProposerSubjectId"/>: that one stamps the
    /// authorial subject at draft creation; this one is the explicit handoff
    /// signal toward an approver.
    /// </summary>
    public bool ReadyForReview { get; set; }

    /// <summary>
    /// Timestamp set when this version transitions to <see cref="LifecycleState.Retired"/>.
    /// P2's lifecycle service stamps it inside the transition transaction; never modified
    /// after retirement. Null for versions that never reached Retired.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>Set when a newer version transitions this one to WindingDown (P2 auto-supersede).</summary>
    public Guid? SupersededByVersionId { get; set; }

    /// <summary>
    /// Optimistic-concurrency token. On Postgres mapped to <c>xmin</c> (system column, no storage cost)
    /// via Npgsql's <c>UseXminAsConcurrencyToken()</c>; on SQLite a plain column bumped in
    /// <c>SaveChangesAsync</c>. Both configured in <see cref="Infrastructure.Data.AppDbContext"/>.
    /// </summary>
    public uint Revision { get; set; }

    /// <summary>
    /// Apply a mutation to this entity inside a Draft-only guard. Throws
    /// <see cref="InvalidOperationException"/> when <see cref="State"/> is not
    /// <see cref="LifecycleState.Draft"/>. Use for in-memory mutations from application code;
    /// the EF <c>SaveChangesAsync</c> guard is the belt-and-braces enforcement at persistence time.
    /// </summary>
    public void MutateDraftField(Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        if (State != LifecycleState.Draft)
        {
            throw new InvalidOperationException(
                $"PolicyVersion {Id} is in state {State}; only Draft versions are mutable.");
        }

        apply();
    }
}
