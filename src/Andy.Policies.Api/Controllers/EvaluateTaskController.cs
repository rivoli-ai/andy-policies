// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.PlanEvaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// Per-task / per-run compliance-evaluation surface for
/// rivoli-ai/conductor#1944 (TX F7.1). The cockpit (via andy-tasks'
/// <c>PlanExecutor</c>) calls <c>POST /api/policies/evaluate-task</c>
/// during execution — once per task / agent run — to re-evaluate the
/// task against the goal's effective policies and obtain a structured
/// <see cref="ComplianceAssessment"/> (violations + risk tier/score),
/// not just the binary plan-finalize verdict. Reuses the same
/// binding-resolution + predicate pipeline as
/// <see cref="EvaluatePlanController"/> (no forked evaluator) and is
/// idempotent per <c>(goalId, taskId, planVersion)</c>.
/// </summary>
[ApiController]
[Route("api/policies")]
[Authorize]
public sealed class EvaluateTaskController : ControllerBase
{
    private readonly IPlanEvaluator _evaluator;

    public EvaluateTaskController(IPlanEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    /// <summary>
    /// Evaluate the task identified by <paramref name="request"/> against
    /// the workspace's policies. Returns:
    /// <list type="bullet">
    ///   <item><c>200</c> with the structured compliance assessment when
    ///     the goal + task resolve and the evaluator reaches a verdict.</item>
    ///   <item><c>400</c> when the request body is missing or either id is
    ///     the empty GUID.</item>
    ///   <item><c>404</c> when andy-tasks doesn't recognise the goal, or
    ///     the goal does not contain the task.</item>
    /// </list>
    /// Idempotent for 5 minutes per <c>(goalId, taskId, planVersion)</c> —
    /// a replanned goal (new <c>planVersion</c>) bypasses the cache.
    /// </summary>
    [HttpPost("evaluate-task")]
    [Authorize(Policy = "andy-policies:plan:evaluate-task")]
    [ProducesResponseType(typeof(EvaluateTaskResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EvaluateTaskResponse>> EvaluateTask(
        [FromBody] EvaluateTaskRequest request, CancellationToken ct)
    {
        if (request is null) return BadRequest("Request body required.");
        if (request.GoalId == Guid.Empty)
        {
            return BadRequest("goalId is required and must not be the empty GUID.");
        }
        if (request.TaskId == Guid.Empty)
        {
            return BadRequest("taskId is required and must not be the empty GUID.");
        }

        var response = await _evaluator.EvaluateTaskAsync(request.GoalId, request.TaskId, ct);
        return response is null ? NotFound() : Ok(response);
    }
}
