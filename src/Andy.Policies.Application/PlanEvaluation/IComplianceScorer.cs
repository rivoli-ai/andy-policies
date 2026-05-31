// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.PlanEvaluation;

/// <summary>
/// Deterministic risk fold over a set of compliance violations
/// (rivoli-ai/conductor#1944 — TX F7.1). Extracted to its own interface
/// so the weight table is unit-testable in isolation: given the same
/// violations (each carrying the firing policy's criticality and the
/// predicate outcome/decision), it always produces the same
/// <see cref="RiskTier"/> and numeric score.
/// </summary>
/// <remarks>
/// Scoring is a fold weighted by:
/// <list type="bullet">
///   <item>the firing policy's <see cref="Severity"/> (criticality —
///     <c>Info</c> &lt; <c>Moderate</c> &lt; <c>Critical</c>), and</item>
///   <item>the violation's decision grade — a <c>reject</c>-firing
///     violation outranks a <c>manual</c>-firing one — with an
///     <c>unevaluable</c> outcome treated conservatively (never a silent
///     pass), mirroring <c>RuleFires</c>' "couldn't prove it passed"
///     semantics.</item>
/// </list>
/// Empty violations ⇒ <see cref="RiskTier.None"/> / score 0.
/// </remarks>
public interface IComplianceScorer
{
    /// <summary>
    /// Fold <paramref name="violations"/> into an aggregate risk tier and
    /// numeric score. Each <see cref="ScoredViolation"/> pairs a wire
    /// <see cref="ComplianceViolation"/> with the criticality of the
    /// policy that fired it.
    /// </summary>
    ComplianceRisk Score(IReadOnlyList<ScoredViolation> violations);
}

/// <summary>
/// A violation paired with the criticality of the policy that fired it —
/// the input unit for <see cref="IComplianceScorer.Score"/>. Kept
/// separate from the wire <see cref="ComplianceViolation"/> so the
/// criticality (an internal weight input) never leaks onto the wire.
/// </summary>
public sealed record ScoredViolation(
    ComplianceViolation Violation,
    Severity Criticality);

/// <summary>Output of <see cref="IComplianceScorer.Score"/>.</summary>
public sealed record ComplianceRisk(RiskTier Tier, int Score);
