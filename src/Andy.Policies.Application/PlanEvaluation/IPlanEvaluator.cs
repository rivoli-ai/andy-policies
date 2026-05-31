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

    /// <summary>
    /// Per-task / per-run evaluation (rivoli-ai/conductor#1944 — TX F7.1).
    /// Reuses the same binding-resolution + predicate pipeline as
    /// <see cref="EvaluatePlanAsync"/>, but scopes the predicate inputs to
    /// the single <paramref name="taskId"/> rather than the whole goal
    /// view, and returns a structured <see cref="ComplianceAssessment"/>
    /// (violations + risk tier/score, plus the back-compat decision
    /// string) instead of the binary plan verdict.
    /// <para>
    /// Returns <c>null</c> when the goal does not exist or when the goal
    /// exists but does not contain <paramref name="taskId"/> — the
    /// controller translates both to 404. The 5-minute idempotency cache
    /// keys on <c>(goalId, taskId, planVersion)</c> so a per-task re-eval
    /// is cached independently and a replan (new planVersion) busts it.
    /// </para>
    /// </summary>
    Task<EvaluateTaskResponse?> EvaluateTaskAsync(
        Guid goalId, Guid taskId, CancellationToken ct = default);
}
