// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.PlanEvaluation;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Andy.Policies.Infrastructure.Docs;

/// <summary>
/// Reference <see cref="IComplianceAuditPublisher"/> for
/// rivoli-ai/conductor#1945 (TX F7.2). Serialises the #1944
/// <see cref="ComplianceAssessment"/> to JSON and (optionally) renders the
/// hash-chained audit-export NDJSON segment via <see cref="IAuditExporter"/>,
/// then writes them into andy-docs as <c>role:Audit</c> documents linked
/// to the goal (and task, where in scope) through <see cref="IDocsClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Best-effort.</b> Every andy-docs interaction is wrapped so a
/// failure logs <c>[ANDY-POLICIES-AUDIT-DOCS-*]</c> and returns
/// <see cref="ComplianceAuditPublishResult.DegradedResult"/> — it never
/// throws past this method, so the caller's policy decision is unaffected.
/// </para>
/// <para>
/// <b>Seq-range bounding.</b> andy-policies' audit chain records catalog
/// mutations, not per-decision events, so a compliance decision does not
/// itself create an audit row. The export segment attached alongside the
/// assessment is therefore bounded to the chain head observed at decision
/// time (<c>fromSeq=null, toSeq=MAX(seq)</c>): the full verifiable chain
/// up to the moment of the verdict. This makes the andy-docs copy
/// tamper-evident (the terminal hash pins chain state at decision time)
/// without inventing a synthetic per-decision event. See
/// <c>docs/reference/compliance-audit-injection.md</c>.
/// </para>
/// </remarks>
public sealed class ComplianceAuditPublisher : IComplianceAuditPublisher
{
    private const string AssessmentMime = "application/json";

    // andy-docs allowlist accepts application/* including application/json;
    // the NDJSON export is line-delimited JSON, sent as application/json so
    // it passes the allowlist (application/x-ndjson is not on the list).
    private const string AuditChainMime = "application/json";

    private const string AuditRole = "Audit";

    private static readonly JsonSerializerOptions AssessmentFormat = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly IDocsClient _docs;
    private readonly IAuditExporter _exporter;
    private readonly AppDbContext _db;
    private readonly ComplianceAuditPublisherOptions _options;
    private readonly ILogger<ComplianceAuditPublisher> _log;

    public ComplianceAuditPublisher(
        IDocsClient docs,
        IAuditExporter exporter,
        AppDbContext db,
        IOptions<ComplianceAuditPublisherOptions> options,
        ILogger<ComplianceAuditPublisher> log)
    {
        _docs = docs;
        _exporter = exporter;
        _db = db;
        _options = options.Value;
        _log = log;
    }

    public Task<ComplianceAuditPublishResult> PublishPlanAsync(
        Guid goalId,
        string planVersion,
        ComplianceAssessment assessment,
        CancellationToken ct = default)
        => PublishAsync(
            goalId,
            taskId: null,
            planVersion,
            assessment,
            links: new[] { new DocsLink(DocsLinkTargetType.Goal, goalId.ToString("D"), AuditRole) },
            ct);

    public Task<ComplianceAuditPublishResult> PublishTaskAsync(
        Guid goalId,
        Guid taskId,
        string planVersion,
        ComplianceAssessment assessment,
        CancellationToken ct = default)
        => PublishAsync(
            goalId,
            taskId,
            planVersion,
            assessment,
            links: new[]
            {
                new DocsLink(DocsLinkTargetType.Goal, goalId.ToString("D"), AuditRole),
                new DocsLink(DocsLinkTargetType.Task, taskId.ToString("D"), AuditRole),
            },
            ct);

    private async Task<ComplianceAuditPublishResult> PublishAsync(
        Guid goalId,
        Guid? taskId,
        string planVersion,
        ComplianceAssessment assessment,
        IReadOnlyList<DocsLink> links,
        CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return ComplianceAuditPublishResult.SkippedResult;
        }

        ArgumentNullException.ThrowIfNull(assessment);

        var scopeTag = taskId is { } tid ? $"task:{tid:D}" : "plan";

        DocsRef? assessmentRef;
        try
        {
            var assessmentBytes = BuildAssessmentBytes(goalId, taskId, planVersion, assessment);
            var assessmentReq = new DocsPutRequest(
                FileName: AssessmentFileName(goalId, taskId),
                MimeType: AssessmentMime,
                Content: assessmentBytes,
                Title: $"Compliance assessment — goal {goalId:D}{(taskId is { } t ? $" / task {t:D}" : string.Empty)}",
                Message: $"andy-policies compliance assessment ({scopeTag}, plan {planVersion})",
                Links: links);
            assessmentRef = await _docs.PutAsync(assessmentReq, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // [ANDY-POLICIES-AUDIT-DOCS-1] assessment upload failed.
            _log.LogWarning(ex,
                "[ANDY-POLICIES-AUDIT-DOCS-1] compliance assessment docs.put failed for goal {GoalId} ({Scope}); audit is degraded but the policy decision is unaffected",
                goalId, scopeTag);
            return ComplianceAuditPublishResult.DegradedResult;
        }

        DocsRef? auditChainRef = null;
        if (_options.IncludeAuditChainSegment)
        {
            try
            {
                auditChainRef = await PublishAuditChainSegmentAsync(
                    goalId, taskId, planVersion, links, scopeTag, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // [ANDY-POLICIES-AUDIT-DOCS-2] audit-chain segment failed.
                // The assessment was written; surface degraded so the
                // signal isn't lost, but keep the assessment ref.
                _log.LogWarning(ex,
                    "[ANDY-POLICIES-AUDIT-DOCS-2] audit-chain segment docs.put failed for goal {GoalId} ({Scope}); assessment was written, tamper-evident segment is degraded",
                    goalId, scopeTag);
                return new ComplianceAuditPublishResult(
                    Skipped: false, Degraded: true, AssessmentRef: assessmentRef, AuditChainRef: null);
            }
        }

        return new ComplianceAuditPublishResult(
            Skipped: false,
            Degraded: false,
            AssessmentRef: assessmentRef,
            AuditChainRef: auditChainRef);
    }

    private async Task<DocsRef> PublishAuditChainSegmentAsync(
        Guid goalId,
        Guid? taskId,
        string planVersion,
        IReadOnlyList<DocsLink> links,
        string scopeTag,
        CancellationToken ct)
    {
        // Chain head at decision time. Empty chain → 0; the exporter
        // still emits a well-formed (empty) bundle with a summary line.
        var headSeq = await _db.AuditEvents
            .AsNoTracking()
            .Select(e => (long?)e.Seq)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? 0L;

        using var buffer = new MemoryStream();
        await _exporter.WriteNdjsonAsync(buffer, fromSeq: null, toSeq: headSeq, ct)
            .ConfigureAwait(false);

        var req = new DocsPutRequest(
            FileName: AuditChainFileName(goalId, taskId),
            MimeType: AuditChainMime,
            Content: buffer.ToArray(),
            Title: $"Audit-chain segment — goal {goalId:D}{(taskId is { } t ? $" / task {t:D}" : string.Empty)} (seq..{headSeq})",
            Message: $"andy-policies hash-chained audit export ({scopeTag}, plan {planVersion}, seq..{headSeq})",
            Links: links);

        return await _docs.PutAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Serialise the assessment plus the identifiers it was computed for
    /// into a self-describing envelope. Round-trips back into
    /// <see cref="ComplianceAssessment"/> via the embedded
    /// <c>assessment</c> object (asserted by the integration round-trip
    /// test).
    /// </summary>
    private static byte[] BuildAssessmentBytes(
        Guid goalId, Guid? taskId, string planVersion, ComplianceAssessment assessment)
    {
        var envelope = new AssessmentEnvelope(
            GoalId: goalId,
            TaskId: taskId,
            PlanVersion: planVersion,
            Assessment: assessment);
        var json = JsonSerializer.Serialize(envelope, AssessmentFormat);
        return Encoding.UTF8.GetBytes(json);
    }

    private static string AssessmentFileName(Guid goalId, Guid? taskId) =>
        taskId is { } t
            ? $"compliance-assessment-{goalId:N}-{t:N}.json"
            : $"compliance-assessment-{goalId:N}.json";

    private static string AuditChainFileName(Guid goalId, Guid? taskId) =>
        taskId is { } t
            ? $"audit-chain-{goalId:N}-{t:N}.ndjson"
            : $"audit-chain-{goalId:N}.ndjson";

    /// <summary>
    /// Self-describing wrapper around the #1944 assessment: carries the
    /// scope ids so an andy-docs reader knows which goal/task/planVersion
    /// the assessment belongs to without consulting the link rows.
    /// </summary>
    private sealed record AssessmentEnvelope(
        Guid GoalId,
        Guid? TaskId,
        string PlanVersion,
        ComplianceAssessment Assessment);
}
