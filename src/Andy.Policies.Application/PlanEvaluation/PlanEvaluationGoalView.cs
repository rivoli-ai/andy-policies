// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.PlanEvaluation;

/// <summary>
/// Service-internal projection of an andy-tasks Goal (with its tasks +
/// estimates + workspace metadata) into the minimal shape required by
/// the plan-aware predicates (rivoli-ai/andy-policies#232).
/// </summary>
/// <remarks>
/// <para>
/// This is intentionally a thin, predicate-facing surface — not a
/// re-shape of every andy-tasks DTO. The <see cref="ITasksPlanClient"/>
/// implementation is responsible for mapping andy-tasks' wire DTOs
/// (<c>GoalDto</c>, <c>TaskDto</c>, <c>EstimatesResponse</c>) into this
/// view; predicates evaluate against this view only.
/// </para>
/// <para>
/// Every collection field is <see cref="IReadOnlyList{T}"/>. Optional
/// fields are nullable; predicates that touch a null field must treat
/// the predicate as failing with reason <c>"data unavailable"</c> per
/// the issue spec — never silently pass when the underlying data is
/// missing.
/// </para>
/// </remarks>
public sealed record PlanEvaluationGoalView(
    Guid GoalId,
    string PlanVersion,
    string? WorkspaceContainerId,
    string? WorkspaceTier,
    IReadOnlyList<string>? WorkspaceApprovedAgentIds,
    decimal? WorkspaceCostBudgetUsd,
    IReadOnlyList<PlanEvaluationTaskView> Tasks,
    decimal? EstimatedCostUsd);

/// <summary>
/// Per-task projection: the predicate-facing slice of an andy-tasks
/// <c>TaskDto</c>. <see cref="ToolsAllowed"/> is the <c>DelegationContract.ToolsAllowed</c>
/// list (lowercase tool slugs); <see cref="EffectiveAgentId"/> is the
/// task's selected agent or, when no executor has been pinned, the
/// first entry of <c>SuggestedAgentIds</c> (consistent with how the
/// caller surfaces "effective agent" elsewhere); <see cref="TargetEnv"/>
/// is the deployment environment the task targets when known
/// (<c>"prod"</c> / <c>"staging"</c> / <c>"dev"</c> / null).
/// </summary>
public sealed record PlanEvaluationTaskView(
    Guid TaskId,
    string? EffectiveAgentId,
    IReadOnlyList<string>? ToolsAllowed,
    string? TargetEnv);
