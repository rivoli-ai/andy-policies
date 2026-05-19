// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.Policies.Infrastructure.Services.PlanEvaluation;

/// <summary>
/// Wire-format extension of <c>PolicyVersion.RulesJson</c> for
/// plan evaluation (rivoli-ai/andy-policies#232). Existing policy DSL
/// blobs (allow/deny lists from the legacy V1 rules) are passed through
/// unchanged; only policies that opt in to plan evaluation populate
/// <see cref="PlanEvaluation"/>. Policies whose <c>RulesJson</c> doesn't
/// declare <c>planEvaluation</c> are ignored by the plan evaluator —
/// they neither approve nor reject and fall through to the default
/// <c>manual</c>.
/// </summary>
/// <remarks>
/// <para>
/// Decision rules are evaluated top-to-bottom: the first rule whose
/// predicate references <b>all</b> pass (the <c>requireAll</c> list) and
/// whose negative-predicate references <b>all</b> fail (the
/// <c>requireNone</c> list) fires. A rule firing with
/// <see cref="DecisionRule.Decision"/> = <c>approve</c> or <c>reject</c>
/// terminates evaluation for that policy — the policy emits that
/// decision. <c>manual</c> rules are allowed for clarity but are
/// equivalent to no rule firing (the policy falls through).
/// </para>
/// <para>
/// A predicate referenced by name but not registered in the predicate
/// catalog causes the rule NOT to fire — same posture as a failed
/// predicate. The evaluator does not throw on unknown predicate names
/// because that would couple the policy authoring surface to the
/// service's predicate registry version.
/// </para>
/// </remarks>
public sealed record PlanEvaluationRules(
    [property: JsonPropertyName("decisionRules")] IReadOnlyList<DecisionRule>? DecisionRules);

public sealed record DecisionRule(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("requireAll")] IReadOnlyList<string>? RequireAll,
    [property: JsonPropertyName("requireNone")] IReadOnlyList<string>? RequireNone);

internal sealed record RulesJsonEnvelope(
    [property: JsonPropertyName("planEvaluation")] PlanEvaluationRules? PlanEvaluation);

public static class PolicyRulesDslParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Pull the <c>planEvaluation</c> block out of a policy version's
    /// opaque <c>RulesJson</c>. Returns <c>null</c> when the policy
    /// doesn't declare plan evaluation rules (existing P1/V1 policies)
    /// or when the JSON is malformed — malformed JSON is logged by the
    /// caller and treated as "policy does not participate in plan
    /// evaluation" rather than 5xx, because the catalog is multi-tenant
    /// and one malformed entry must not break evaluation for the rest.
    /// </summary>
    public static PlanEvaluationRules? TryParse(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson)) return null;
        try
        {
            var env = JsonSerializer.Deserialize<RulesJsonEnvelope>(rulesJson, Options);
            return env?.PlanEvaluation;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
