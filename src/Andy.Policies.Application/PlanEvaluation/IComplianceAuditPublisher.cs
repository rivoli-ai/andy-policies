// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;

namespace Andy.Policies.Application.PlanEvaluation;

/// <summary>
/// Persists a compliance decision into andy-docs as <c>role:Audit</c>
/// document(s) (rivoli-ai/conductor#1945 — TX F7.2). Invoked from the
/// evaluation pipeline on plan-finalize and per-task evaluation. Writes
/// two artifacts, both <c>role:Audit</c>, linked to the goal (and the
/// task, where one is in scope):
/// <list type="number">
///   <item>the structured <see cref="ComplianceAssessment"/> as
///     <c>application/json</c>;</item>
///   <item>the matching hash-chained audit-export NDJSON segment
///     (bounded to the seq range covering the decision), making the
///     andy-docs copy tamper-evident.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Best-effort.</b> Publishing is decoupled from the policy verdict —
/// a <c>docs.put</c> failure (or a disabled publisher) must never block
/// or change the decision the caller returns. Failures are logged with a
/// source code and surfaced as a degraded-audit signal
/// (<see cref="ComplianceAuditPublishResult.Degraded"/>), never silently
/// swallowed.
/// </para>
/// </remarks>
public interface IComplianceAuditPublisher
{
    /// <summary>
    /// Publish the audit record for a plan-finalize decision. Links the
    /// document to <c>{ Goal, goalId, Audit }</c>.
    /// </summary>
    Task<ComplianceAuditPublishResult> PublishPlanAsync(
        Guid goalId,
        string planVersion,
        ComplianceAssessment assessment,
        CancellationToken ct = default);

    /// <summary>
    /// Publish the audit record for a per-task decision. Links the
    /// document to <c>{ Goal, goalId, Audit }</c> and
    /// <c>{ Task, taskId, Audit }</c>.
    /// </summary>
    Task<ComplianceAuditPublishResult> PublishTaskAsync(
        Guid goalId,
        Guid taskId,
        string planVersion,
        ComplianceAssessment assessment,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of a publish attempt. <see cref="Skipped"/> is true when the
/// publisher is disabled by config (shipped-dark mode); otherwise either
/// the refs are populated or <see cref="Degraded"/> is true with the
/// failure logged. The refs are the join key Conductor reads back via
/// the by-target link query.
/// </summary>
/// <param name="Skipped">True when publishing is disabled by config.</param>
/// <param name="Degraded">
/// True when an attempt was made but failed (docs unreachable / non-2xx);
/// the policy decision is unaffected.
/// </param>
/// <param name="AssessmentRef">The DocsRef for the assessment JSON, when written.</param>
/// <param name="AuditChainRef">The DocsRef for the NDJSON export segment, when written.</param>
public sealed record ComplianceAuditPublishResult(
    bool Skipped,
    bool Degraded,
    DocsRef? AssessmentRef,
    DocsRef? AuditChainRef)
{
    /// <summary>A no-op result for the disabled (shipped-dark) path.</summary>
    public static ComplianceAuditPublishResult SkippedResult { get; } =
        new(Skipped: true, Degraded: false, AssessmentRef: null, AuditChainRef: null);

    /// <summary>A degraded result — an attempt failed; decision unaffected.</summary>
    public static ComplianceAuditPublishResult DegradedResult { get; } =
        new(Skipped: false, Degraded: true, AssessmentRef: null, AuditChainRef: null);
}
