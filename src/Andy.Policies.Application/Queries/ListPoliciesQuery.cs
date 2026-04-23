// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Queries;

/// <summary>
/// Filters + pagination for <c>IPolicyService.ListPoliciesAsync</c>. Filters match
/// against the <i>active version</i> of each policy (per P1's "highest non-Draft"
/// rule); policies with no active version are excluded from filtered results.
/// </summary>
/// <param name="NamePrefix">Optional case-sensitive prefix match on <c>Policy.Name</c>.</param>
/// <param name="Scope">Optional membership test against the active version's scopes.</param>
/// <param name="Enforcement">Optional filter on the active version's enforcement posture.</param>
/// <param name="Severity">Optional filter on the active version's severity tier.</param>
/// <param name="Skip">Offset for pagination (default 0).</param>
/// <param name="Take">Page size (default 100, capped at 500).</param>
public record ListPoliciesQuery(
    string? NamePrefix = null,
    string? Scope = null,
    string? Enforcement = null,
    string? Severity = null,
    int Skip = 0,
    int Take = 100);
