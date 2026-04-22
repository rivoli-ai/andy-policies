// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;

namespace Andy.Policies.Domain.Validation;

/// <summary>
/// Validation + canonicalisation for the flat <c>Scopes</c> list on <see cref="Entities.PolicyVersion"/>.
/// </summary>
/// <remarks>
/// Full service-layer validation (length caps, dedup + sort, persistence-time enforcement)
/// lands in P1.4 <c>IPolicyService</c>. P1.2 ships this helper so the regex contract is
/// unit-testable now and reusable by the service layer without re-invention.
///
/// Reserved wildcard <c>*</c> is refused — hierarchy semantics land in Epic P4
/// (rivoli-ai/andy-policies#4).
/// </remarks>
public static partial class PolicyScope
{
    /// <summary>
    /// Canonical scope-token shape: lowercase, starts with a letter, may contain
    /// lowercase letters, digits, and the separators <c>:</c> <c>.</c> <c>_</c> <c>-</c>.
    /// Length 1-63. Example: <c>prod</c>, <c>repo:rivoli-ai-andy-policies</c>,
    /// <c>tool:write-branch</c>, <c>template:code-change</c>.
    /// </summary>
    /// <remarks>
    /// Note: the regex does NOT permit <c>/</c>. Consumers that need repo-slug shape
    /// <c>repo:rivoli-ai/andy-policies</c> use binding <c>targetRef</c> instead
    /// (ADR-tracked in P3.1 authoritative targetRef convention) — scopes are coarser
    /// tags for policy applicability, not precise repo identifiers.
    /// </remarks>
    public const string RegexPattern = "^[a-z][a-z0-9:._-]{0,62}$";

    [GeneratedRegex(RegexPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ScopeRegex();

    public const string ReservedWildcard = "*";

    /// <summary>
    /// Returns true if <paramref name="scope"/> is a syntactically valid scope token.
    /// The reserved wildcard <c>*</c> is explicitly invalid here (reserved for Epic P4).
    /// </summary>
    public static bool IsValid(string? scope)
    {
        if (string.IsNullOrEmpty(scope)) return false;
        if (scope == ReservedWildcard) return false;
        return ScopeRegex().IsMatch(scope);
    }

    /// <summary>
    /// Canonicalise a scope list: distinct + ordinal-sorted. Throws
    /// <see cref="ArgumentException"/> if any element fails <see cref="IsValid"/>.
    /// </summary>
    public static IReadOnlyList<string> Canonicalise(IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var list = new List<string>();
        foreach (var s in scopes)
        {
            if (!IsValid(s))
            {
                throw new ArgumentException(
                    $"Scope '{s}' is invalid. Expected pattern {RegexPattern}, reserved wildcard '*' is disallowed.",
                    nameof(scopes));
            }

            list.Add(s);
        }

        return list
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }
}
