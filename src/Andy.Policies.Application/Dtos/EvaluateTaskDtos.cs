// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Inbound request for <c>POST /api/policies/evaluate-task</c>
/// (rivoli-ai/conductor#1944 — TX F7.1). The cockpit (via andy-tasks'
/// <c>PlanExecutor</c>) calls this per task / per agent run during
/// execution — governance during execution, not just at plan finalize.
/// The service fetches the goal view from andy-tasks, scopes the
/// predicate inputs down to the single <see cref="TaskId"/>, resolves the
/// same effective policy set the plan got, and returns a structured
/// <see cref="ComplianceAssessment"/>.
/// </summary>
/// <param name="GoalId">The owning goal (resolves the binding chain).</param>
/// <param name="TaskId">The task to scope the evaluation to.</param>
public sealed record EvaluateTaskRequest(Guid GoalId, Guid TaskId);

/// <summary>
/// Result of a per-task evaluation: a structured
/// <see cref="ComplianceAssessment"/> plus the identifiers it was scoped
/// to. The assessment embeds the back-compat decision string so
/// plan-finalize-style callers can read a verdict, while #1945/#1946 read
/// the violations + risk tier/score.
/// </summary>
/// <param name="GoalId">The goal the assessment was computed for.</param>
/// <param name="TaskId">The task the assessment was scoped to.</param>
/// <param name="Assessment">The structured compliance assessment.</param>
public sealed record EvaluateTaskResponse(
    Guid GoalId,
    Guid TaskId,
    ComplianceAssessment Assessment);
