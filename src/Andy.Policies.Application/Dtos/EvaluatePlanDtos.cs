// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Inbound request for <c>POST /api/policies/evaluate-plan</c>
/// (rivoli-ai/andy-policies#232, companion to rivoli-ai/conductor#1633
/// SP.4.2). andy-tasks calls this on plan finalize. The service fetches
/// the goal + tasks back from andy-tasks via M2M, projects them into a
/// <c>PlanEvaluationGoalView</c>, runs the workspace's policies over
/// the registered plan-aware predicates, and returns a single
/// approve/manual/reject decision plus the predicate trace.
/// </summary>
public sealed record EvaluatePlanRequest(Guid GoalId);

/// <summary>
/// Result of evaluating a plan against the workspace's policies.
/// <see cref="Decision"/> is the wire-stable lowercase string
/// <c>approve</c> / <c>manual</c> / <c>reject</c>; <see cref="PolicyId"/>
/// is the policy that fired the approve/reject decision (null when the
/// decision degraded to the default <c>manual</c>); <see cref="Predicates"/>
/// is the full evaluation trace — every predicate evaluated against
/// the plan, regardless of which policy used it.
/// </summary>
public sealed record EvaluatePlanResponse(
    string Decision,
    string? PolicyId,
    IReadOnlyList<PredicateResultDto> Predicates);

/// <summary>
/// One predicate evaluation result. <see cref="Passed"/> is the
/// raw boolean; <see cref="Reason"/> carries a human-readable
/// explanation, especially in the negative case (e.g.
/// <c>"data unavailable"</c> when the predicate could not evaluate
/// because the goal view was missing the relevant field — per the
/// issue spec, missing data must never be treated as silently passing).
/// </summary>
public sealed record PredicateResultDto(
    string Name,
    bool Passed,
    string Reason);
