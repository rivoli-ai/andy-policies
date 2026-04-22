// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Enums;

/// <summary>
/// Incident / triage tier for a <see cref="Entities.PolicyVersion"/>.
/// </summary>
/// <remarks>
/// This service only stores the tier — it does NOT page anyone or apply SLAs.
/// Consumers own the downstream behaviour:
/// <list type="bullet">
///   <item><term>Info</term><description>no alert; surfaced in dashboards only.</description></item>
///   <item><term>Moderate</term><description>logged for review; counts against quotas.</description></item>
///   <item><term>Critical</term><description>Conductor admission blocks; may trigger paging via
///     consumer-side policy (rivoli-ai/andy-tasks Epic AD audit bundle signing).</description></item>
/// </list>
/// Per ADR 0001 §6 the wire format is lowercase (`info` / `moderate` / `critical`)
/// — matches the criticality mapping carried over from rivoli-ai/andy-rbac#18
/// reconciliation for the stock policies (<c>read-only</c> → Info,
/// <c>write-branch</c>/<c>sandboxed</c> → Moderate, <c>no-prod</c>/<c>high-risk</c> → Critical).
/// </remarks>
public enum Severity
{
    Info = 0,
    Moderate = 1,
    Critical = 2,
}
