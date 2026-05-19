// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.PlanEvaluation;

/// <summary>
/// One plan-aware predicate (rivoli-ai/andy-policies#232 DSL extension).
/// Implementations are pure functions of the projected
/// <see cref="PlanEvaluationGoalView"/> — no I/O, no DI lookups at evaluate
/// time. The set of registered predicates is the catalog the policy DSL
/// can reference by <see cref="Name"/>.
/// </summary>
/// <remarks>
/// Contract: when the predicate cannot be evaluated against the given
/// view (e.g. cost data is missing for <c>respectsCostBudget</c>), it
/// MUST return <see cref="PredicateEvaluation.Unevaluable"/> — never
/// silently treat missing data as a pass. The evaluator surfaces these
/// as <c>passed: false, reason: "data unavailable"</c> on the wire so
/// the caller can react.
/// </remarks>
public interface IPlanPredicate
{
    /// <summary>
    /// Wire-stable identifier (camelCase, e.g. <c>allTasksReadOnly</c>).
    /// Pinned by the issue body; do not rename without a contract bump.
    /// </summary>
    string Name { get; }

    PredicateEvaluation Evaluate(PlanEvaluationGoalView view);
}

/// <summary>
/// Tri-state result of a predicate. <see cref="Pass"/>/<see cref="Fail"/>
/// carry an explanatory reason; <see cref="Unevaluable"/> means the
/// required data was missing from the projected view. The evaluator
/// collapses <see cref="Unevaluable"/> to <c>passed: false</c> on the
/// wire — never to <c>true</c>.
/// </summary>
public readonly record struct PredicateEvaluation(
    PredicateOutcome Outcome,
    string Reason)
{
    public static PredicateEvaluation Pass(string reason) =>
        new(PredicateOutcome.Pass, reason);

    public static PredicateEvaluation Fail(string reason) =>
        new(PredicateOutcome.Fail, reason);

    public static PredicateEvaluation Unevaluable(string reason = "data unavailable") =>
        new(PredicateOutcome.Unevaluable, reason);
}

public enum PredicateOutcome
{
    Pass,
    Fail,
    Unevaluable,
}
