// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.PlanEvaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// Plan-evaluation surface for rivoli-ai/andy-policies#232 (companion
/// to rivoli-ai/conductor#1633 SP.4.2). andy-tasks calls
/// <c>POST /api/policies/evaluate-plan</c> on plan finalize to decide
/// whether the plan can auto-approve, must surface to a human, or is
/// rejected outright. The endpoint itself is intentionally narrow —
/// idempotent per <c>(goalId, planVersion)</c>, no side effects beyond
/// the 5-minute decision cache, no audit row of its own (the caller
/// records the decision as it pipes it into the goal lifecycle).
/// </summary>
[ApiController]
[Route("api/policies")]
[Authorize]
public sealed class EvaluatePlanController : ControllerBase
{
    private readonly IPlanEvaluator _evaluator;

    public EvaluatePlanController(IPlanEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    /// <summary>
    /// Evaluate the plan for <paramref name="request"/>.GoalId against
    /// the workspace's policies. Returns:
    /// <list type="bullet">
    ///   <item><c>200</c> with the decision + predicate trace when the
    ///     goal resolves and the evaluator reaches a verdict.</item>
    ///   <item><c>400</c> when the request body is missing or
    ///     <c>GoalId</c> is the empty GUID.</item>
    ///   <item><c>404</c> when andy-tasks doesn't recognise the goal.</item>
    /// </list>
    /// Idempotent for 5 minutes per <c>(goalId, planVersion)</c> — a
    /// second call within the window returns the cached response
    /// verbatim, including the same predicate trace; a replanned goal
    /// (new <c>planVersion</c>) bypasses the cache.
    /// </summary>
    [HttpPost("evaluate-plan")]
    [Authorize(Policy = "andy-policies:plan:evaluate")]
    [ProducesResponseType(typeof(EvaluatePlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EvaluatePlanResponse>> EvaluatePlan(
        [FromBody] EvaluatePlanRequest request, CancellationToken ct)
    {
        if (request is null) return BadRequest("Request body required.");
        if (request.GoalId == Guid.Empty)
        {
            return BadRequest("goalId is required and must not be the empty GUID.");
        }

        var response = await _evaluator.EvaluatePlanAsync(request.GoalId, ct);
        return response is null ? NotFound() : Ok(response);
    }
}
