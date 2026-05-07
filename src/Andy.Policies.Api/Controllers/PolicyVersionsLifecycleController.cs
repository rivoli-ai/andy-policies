// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// REST surface for lifecycle transitions on a <c>PolicyVersion</c> (P2.3, story
/// rivoli-ai/andy-policies#13). Three action-shaped endpoints — <c>publish</c>,
/// <c>winding-down</c>, <c>retire</c> — sit on top of
/// <see cref="ILifecycleTransitionService"/>. Auto-supersede of the previous
/// Active happens inside the service's serializable transaction; the controller
/// is a thin wire-format adapter and never re-implements state-machine logic.
/// Service exceptions map to status codes via <c>PolicyExceptionHandler</c>:
/// <see cref="Application.Exceptions.ValidationException"/> → 400 (rationale
/// missing), <see cref="Application.Exceptions.NotFoundException"/> → 404
/// (unknown id), <see cref="Application.Exceptions.InvalidLifecycleTransitionException"/>
/// and <see cref="Application.Exceptions.ConcurrentPublishException"/> → 409.
/// </summary>
[ApiController]
[Authorize]
[Route("api/policies/{id:guid}/versions/{versionId:guid}")]
[Produces("application/json")]
public sealed class PolicyVersionsLifecycleController : ControllerBase
{
    private readonly ILifecycleTransitionService _transitions;

    public PolicyVersionsLifecycleController(ILifecycleTransitionService transitions)
    {
        _transitions = transitions;
    }

    /// <summary>
    /// Promote a Draft to Active. Auto-supersedes the existing Active version
    /// (if any) within the same DB transaction.
    /// </summary>
    [HttpPost("publish")]
    [Authorize(Policy = "andy-policies:policy:publish")]
    [ProducesResponseType(typeof(PolicyVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<ActionResult<PolicyVersionDto>> Publish(
        Guid id, Guid versionId,
        [FromBody] LifecycleTransitionRequest body,
        CancellationToken ct)
        => ExecuteAsync(id, versionId, LifecycleState.Active, body, ct);

    /// <summary>
    /// Mark an Active version as winding down. Reads against the previous
    /// Active continue to resolve until the version is retired.
    /// </summary>
    [HttpPost("winding-down")]
    [Authorize(Policy = "andy-policies:policy:transition")]
    [ProducesResponseType(typeof(PolicyVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<ActionResult<PolicyVersionDto>> WindDown(
        Guid id, Guid versionId,
        [FromBody] LifecycleTransitionRequest body,
        CancellationToken ct)
        => ExecuteAsync(id, versionId, LifecycleState.WindingDown, body, ct);

    /// <summary>
    /// Tombstone a version. Stamps <c>RetiredAt</c>; subsequent transitions are
    /// rejected by the matrix.
    /// </summary>
    [HttpPost("retire")]
    [Authorize(Policy = "andy-policies:policy:transition")]
    [ProducesResponseType(typeof(PolicyVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<ActionResult<PolicyVersionDto>> Retire(
        Guid id, Guid versionId,
        [FromBody] LifecycleTransitionRequest body,
        CancellationToken ct)
        => ExecuteAsync(id, versionId, LifecycleState.Retired, body, ct);

    private async Task<ActionResult<PolicyVersionDto>> ExecuteAsync(
        Guid id, Guid versionId, LifecycleState target,
        LifecycleTransitionRequest? body, CancellationToken ct)
    {
        // Per #13 security firewall: never write a fallback subject id into the
        // catalog. JwtBearer maps `sub` to NameIdentifier; TestAuthHandler sets
        // the Name claim. If neither is present, [Authorize] should already have
        // returned 401 — this is the belt to the framework's braces.
        var subjectId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name;
        if (string.IsNullOrEmpty(subjectId))
        {
            return Unauthorized();
        }

        var dto = await _transitions.TransitionAsync(
            id, versionId, target,
            body?.Rationale ?? string.Empty,
            subjectId,
            body?.ExpectedRevision,
            ct);

        return Ok(dto);
    }
}
