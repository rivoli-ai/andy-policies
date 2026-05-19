// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;

namespace Andy.Policies.Application.PlanEvaluation;

/// <summary>
/// Top-level orchestrator for <c>POST /api/policies/evaluate-plan</c>
/// (rivoli-ai/andy-policies#232). Implementations:
/// <list type="number">
///   <item>Fetch the goal view from andy-tasks via <see cref="ITasksPlanClient"/>.</item>
///   <item>Resolve the workspace's applicable policies via the existing
///     binding-resolution pipeline (P4.3).</item>
///   <item>Evaluate every registered <see cref="IPlanPredicate"/> against
///     the view.</item>
///   <item>Apply the per-policy decision rules from <c>RulesJson</c> —
///     first policy that fires <c>approve</c> or <c>reject</c> wins;
///     default <c>manual</c>.</item>
///   <item>Cache the resulting decision for 5 minutes keyed on
///     <c>(GoalId, PlanVersion)</c>.</item>
/// </list>
/// Returns <c>null</c> when the goal does not exist; the controller
/// translates that to 404.
/// </summary>
public interface IPlanEvaluator
{
    Task<EvaluatePlanResponse?> EvaluatePlanAsync(
        Guid goalId, CancellationToken ct = default);
}
