// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.PlanEvaluation;
using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Infrastructure.Services.PlanEvaluation;

/// <summary>
/// Reference <see cref="IComplianceScorer"/> (rivoli-ai/conductor#1944).
/// </summary>
/// <remarks>
/// <para>
/// Deterministic fold. Each violation contributes
/// <c>criticalityWeight × decisionWeight</c>; the total is the numeric
/// risk score. The aggregate <see cref="RiskTier"/> is the max tier any
/// single violation reaches, monotonically bumped up one step when many
/// lower-grade violations accumulate (so a swarm of low-grade manual
/// violations still escalates, but never above a single reject-on-critical).
/// </para>
/// <para>
/// Weight table (pinned by the issue + documented in
/// <c>docs/reference/evaluate-task.md</c>):
/// </para>
/// <list type="table">
///   <listheader><term>criticality</term><description>weight</description></listheader>
///   <item><term>Info</term><description>1</description></item>
///   <item><term>Moderate</term><description>2</description></item>
///   <item><term>Critical</term><description>4</description></item>
/// </list>
/// <list type="table">
///   <listheader><term>decision grade</term><description>weight</description></listheader>
///   <item><term>manual</term><description>2</description></item>
///   <item><term>reject</term><description>5</description></item>
/// </list>
/// An <c>unevaluable</c> outcome does not lower the weight — it is scored
/// at the firing rule's decision grade, exactly like a <c>fail</c>, so
/// missing data never silently reduces risk.
/// </remarks>
public sealed class ComplianceScorer : IComplianceScorer
{
    internal const int RejectWeight = 5;
    internal const int ManualWeight = 2;

    public ComplianceRisk Score(IReadOnlyList<ScoredViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);

        if (violations.Count == 0)
        {
            return new ComplianceRisk(RiskTier.None, 0);
        }

        var score = 0;
        var baseTier = RiskTier.None;

        foreach (var sv in violations)
        {
            var criticalityWeight = CriticalityWeight(sv.Criticality);
            var decisionWeight = DecisionWeight(sv.Violation.Decision);
            score += criticalityWeight * decisionWeight;

            var tier = TierFor(sv.Criticality, sv.Violation.Decision);
            if (tier > baseTier) baseTier = tier;
        }

        // Accumulation bump: more than one violation escalates one step
        // (capped at Critical) — a single moderate-manual violation is
        // Medium, but several of them together warrant a closer look.
        var tierResult = baseTier;
        if (violations.Count > 1 && tierResult is > RiskTier.None and < RiskTier.Critical)
        {
            tierResult = (RiskTier)((int)tierResult + 1);
        }

        return new ComplianceRisk(tierResult, score);
    }

    private static int CriticalityWeight(Severity criticality) => criticality switch
    {
        Severity.Critical => 4,
        Severity.Moderate => 2,
        Severity.Info => 1,
        _ => 1,
    };

    private static int DecisionWeight(string decision) =>
        IsReject(decision) ? RejectWeight : ManualWeight;

    /// <summary>
    /// Per-violation tier before the accumulation bump. A reject on a
    /// critical policy is the worst single violation possible
    /// (<see cref="RiskTier.Critical"/>); a manual on an info policy is
    /// the mildest (<see cref="RiskTier.Low"/>).
    /// </summary>
    private static RiskTier TierFor(Severity criticality, string decision)
    {
        var reject = IsReject(decision);
        return criticality switch
        {
            Severity.Critical => reject ? RiskTier.Critical : RiskTier.High,
            Severity.Moderate => reject ? RiskTier.High : RiskTier.Medium,
            Severity.Info => reject ? RiskTier.Medium : RiskTier.Low,
            _ => reject ? RiskTier.Medium : RiskTier.Low,
        };
    }

    private static bool IsReject(string decision) =>
        string.Equals(decision, "reject", StringComparison.OrdinalIgnoreCase);
}
