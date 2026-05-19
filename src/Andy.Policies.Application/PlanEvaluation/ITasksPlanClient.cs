// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.PlanEvaluation;

/// <summary>
/// Outbound consumer of andy-tasks (rivoli-ai/andy-policies#232).
/// Fetches a goal + its tasks + plan-time estimates + workspace
/// metadata over the existing M2M bearer pipeline, then projects them
/// into a <see cref="PlanEvaluationGoalView"/> the predicates can
/// evaluate against. Returns <c>null</c> when the goal does not exist
/// (controller surfaces 404); throws for transport / authz failures so
/// they surface as a 5xx the caller can retry rather than a misleading
/// <c>manual</c> default.
/// </summary>
public interface ITasksPlanClient
{
    Task<PlanEvaluationGoalView?> FetchGoalViewAsync(
        Guid goalId, CancellationToken ct = default);
}
