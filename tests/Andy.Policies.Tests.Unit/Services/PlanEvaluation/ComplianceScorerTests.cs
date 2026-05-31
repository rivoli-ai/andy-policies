// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.PlanEvaluation;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Services.PlanEvaluation;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services.PlanEvaluation;

/// <summary>
/// Unit tests for <see cref="ComplianceScorer"/> — the deterministic
/// risk fold over compliance violations (rivoli-ai/conductor#1944 —
/// TX F7.1). The scorer is isolated from the evaluator so the weight
/// table can be asserted directly: empty → none/0; single
/// reject-on-critical → critical; criticality weighting; determinism;
/// and that an unevaluable outcome is scored exactly like a fail
/// (never a silent pass).
/// </summary>
public class ComplianceScorerTests
{
    private readonly ComplianceScorer _scorer = new();

    [Fact]
    public void Empty_violations_produces_none_tier_and_zero_score()
    {
        var result = _scorer.Score(Array.Empty<ScoredViolation>());

        result.Tier.Should().Be(RiskTier.None);
        result.Score.Should().Be(0);
    }

    [Fact]
    public void Single_reject_on_critical_policy_is_critical_tier()
    {
        var result = _scorer.Score(new[]
        {
            Sv(Severity.Critical, decision: "reject", outcome: "fail"),
        });

        result.Tier.Should().Be(RiskTier.Critical);
        // 4 (critical) * 5 (reject) = 20
        result.Score.Should().Be(20);
    }

    [Fact]
    public void Single_manual_on_info_policy_is_low_tier()
    {
        var result = _scorer.Score(new[]
        {
            Sv(Severity.Info, decision: "manual", outcome: "fail"),
        });

        result.Tier.Should().Be(RiskTier.Low);
        // 1 (info) * 2 (manual) = 2
        result.Score.Should().Be(2);
    }

    [Fact]
    public void Single_manual_on_moderate_policy_is_medium_tier()
    {
        var result = _scorer.Score(new[]
        {
            Sv(Severity.Moderate, decision: "manual", outcome: "fail"),
        });

        result.Tier.Should().Be(RiskTier.Medium);
        result.Score.Should().Be(4); // 2 * 2
    }

    [Fact]
    public void Reject_outranks_manual_within_same_criticality()
    {
        var reject = _scorer.Score(new[] { Sv(Severity.Info, "reject", "fail") });
        var manual = _scorer.Score(new[] { Sv(Severity.Info, "manual", "fail") });

        ((int)reject.Tier).Should().BeGreaterThan((int)manual.Tier);
        reject.Score.Should().BeGreaterThan(manual.Score);
    }

    [Fact]
    public void Criticality_weighting_increases_score_monotonically()
    {
        var info = _scorer.Score(new[] { Sv(Severity.Info, "reject", "fail") }).Score;
        var moderate = _scorer.Score(new[] { Sv(Severity.Moderate, "reject", "fail") }).Score;
        var critical = _scorer.Score(new[] { Sv(Severity.Critical, "reject", "fail") }).Score;

        info.Should().BeLessThan(moderate);
        moderate.Should().BeLessThan(critical);
    }

    [Fact]
    public void Mixed_manual_and_unevaluable_escalates_tier_by_accumulation()
    {
        // Two moderate-manual violations (each Medium alone) accumulate to
        // High via the >1-violation bump.
        var result = _scorer.Score(new[]
        {
            Sv(Severity.Moderate, "manual", "fail"),
            Sv(Severity.Moderate, "manual", "unevaluable"),
        });

        result.Tier.Should().Be(RiskTier.High);
        result.Score.Should().Be(8); // (2*2) + (2*2)
    }

    [Fact]
    public void Unevaluable_outcome_is_scored_identically_to_fail()
    {
        // Missing data must never silently reduce risk — an unevaluable
        // outcome carries the same weight as an explicit fail.
        var fail = _scorer.Score(new[] { Sv(Severity.Critical, "reject", "fail") });
        var unevaluable = _scorer.Score(new[] { Sv(Severity.Critical, "reject", "unevaluable") });

        unevaluable.Tier.Should().Be(fail.Tier);
        unevaluable.Score.Should().Be(fail.Score);
        unevaluable.Tier.Should().Be(RiskTier.Critical);
    }

    [Fact]
    public void Accumulation_bump_never_exceeds_critical()
    {
        var result = _scorer.Score(new[]
        {
            Sv(Severity.Critical, "reject", "fail"),
            Sv(Severity.Critical, "reject", "fail"),
            Sv(Severity.Critical, "reject", "fail"),
        });

        result.Tier.Should().Be(RiskTier.Critical);
        result.Score.Should().Be(60); // 3 * 20
    }

    [Fact]
    public void Scoring_is_deterministic_for_identical_input()
    {
        var input = new[]
        {
            Sv(Severity.Critical, "reject", "fail"),
            Sv(Severity.Moderate, "manual", "unevaluable"),
            Sv(Severity.Info, "manual", "fail"),
        };

        var first = _scorer.Score(input);
        var second = _scorer.Score(input);

        first.Should().Be(second);
    }

    private static ScoredViolation Sv(Severity criticality, string decision, string outcome) =>
        new(
            new ComplianceViolation(
                PolicyKey: "p",
                Predicate: "somePredicate",
                Outcome: outcome,
                Decision: decision,
                Reason: outcome == "unevaluable" ? "data unavailable" : "predicate failed"),
            criticality);
}
