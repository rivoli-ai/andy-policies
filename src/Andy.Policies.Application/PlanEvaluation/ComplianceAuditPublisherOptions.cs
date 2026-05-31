// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.PlanEvaluation;

/// <summary>
/// Configuration for the andy-docs compliance/audit injection
/// (rivoli-ai/conductor#1945 — TX F7.2). Bound from the
/// <c>ComplianceAudit</c> configuration section.
/// </summary>
/// <remarks>
/// Ships dark: <see cref="Enabled"/> defaults to <c>false</c> so the
/// publisher is wired but inert until an operator flips
/// <c>ComplianceAudit:Enabled=true</c>. When disabled, the publisher
/// returns <see cref="ComplianceAuditPublishResult.SkippedResult"/>
/// without touching andy-docs.
/// </remarks>
public sealed class ComplianceAuditPublisherOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ComplianceAudit";

    /// <summary>
    /// Master switch. When false the publisher is inert (skipped).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true (the default), also attach the hash-chained
    /// audit-export NDJSON segment as a second <c>role:Audit</c>
    /// document, making the andy-docs copy tamper-evident. When false,
    /// only the structured assessment JSON is written.
    /// </summary>
    public bool IncludeAuditChainSegment { get; set; } = true;
}
