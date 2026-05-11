// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Resolves the <c>andy.policies.auditRetentionDays</c> setting into a
/// concrete staleness threshold for the two consumers defined by ADR
/// 0006.1: P6.6 list defaulting and P6.7 export annotation. The
/// setting is metadata-only — it never causes rows to move or
/// disappear from <c>audit_events</c>, and <c>/api/audit/verify</c>
/// MUST NOT consult this policy (verification scope is the integrity
/// contract).
/// </summary>
public interface IAuditRetentionPolicy
{
    /// <summary>
    /// Returns the cut-off timestamp such that events with
    /// <c>Timestamp &lt; threshold</c> are considered stale, or
    /// <c>null</c> when retention is disabled
    /// (<c>auditRetentionDays = 0</c>, the shipped default). Callers
    /// that pass an explicit <c>from</c> filter must prefer the
    /// caller-supplied value over this threshold.
    /// </summary>
    DateTimeOffset? GetStalenessThreshold(DateTimeOffset now);
}
