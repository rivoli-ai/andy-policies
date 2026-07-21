// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Caller identity context used when applying principal/cohort overrides
/// to an effective-policy result. A missing context deliberately means
/// "resolve the baseline binding set without overrides".
/// </summary>
public sealed record OverrideResolutionContext(
    string? PrincipalSubjectId,
    IReadOnlyList<string> CohortRefs)
{
    public static OverrideResolutionContext ForPrincipal(string subjectId) =>
        new(subjectId, Array.Empty<string>());
}
