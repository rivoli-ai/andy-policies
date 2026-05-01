// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Api.Filters;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// REST surface for <c>Override</c> propose/approve/revoke + list/get
/// (P5.5, story rivoli-ai/andy-policies#58). Six endpoints sit on top
/// of <see cref="IOverrideService"/>; the controller is a thin
/// wire-format adapter and never re-implements the state machine.
/// Service exceptions map via <c>PolicyExceptionHandler</c>:
/// <see cref="Application.Exceptions.ValidationException"/> → 400,
/// <see cref="Application.Exceptions.NotFoundException"/> → 404,
/// <see cref="Application.Exceptions.ConflictException"/> → 409,
/// <see cref="Application.Exceptions.SelfApprovalException"/> → 403
/// (errorCode <c>override.self_approval_forbidden</c>),
/// <see cref="Application.Exceptions.RbacDeniedException"/> → 403
/// (errorCode <c>rbac.denied</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Settings gate (P5.4):</b> the three write endpoints carry
/// <see cref="OverrideWriteGateAttribute"/>; reads bypass the gate so
/// the resolution algorithm can still consult existing approved
/// overrides when <c>andy.policies.experimentalOverridesEnabled</c>
/// is off (a security firewall — flipping the toggle off must not
/// strand consumers that already rely on an override's effect).
/// </para>
/// <para>
/// <b>RBAC (P7.2, #51):</b> per-action authorization (e.g.
/// <c>andy-policies:override:approve</c>) is enforced inside
/// <see cref="IOverrideService"/> via <see cref="IRbacChecker"/>.
/// When the andy-rbac client lands, the same code path picks it up
/// without changes here. The controller stays at simple
/// <c>[Authorize]</c>; tightening to per-action policies happens with
/// P7's policy-handler wiring.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/overrides")]
[Produces("application/json")]
[Tags("Overrides")]
public sealed class OverridesController : ControllerBase
{
    private readonly IOverrideService _service;

    public OverridesController(IOverrideService service)
    {
        _service = service;
    }

    /// <summary>
    /// Propose a new override. Inserts in <c>Proposed</c> state; the
    /// approver (a different subject) drives the next transition via
    /// <c>POST /api/overrides/{id}/approve</c>.
    /// </summary>
    [HttpPost]
    [OverrideWriteGate]
    [ProducesResponseType(typeof(OverrideDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OverrideDto>> Propose(
        [FromBody] ProposeOverrideRequest request,
        CancellationToken ct)
    {
        var subject = ResolveSubjectId();
        if (subject is null) return Unauthorized();

        var dto = await _service.ProposeAsync(request, subject, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>
    /// Approve a <c>Proposed</c> override. Returns 403 with errorCode
    /// <c>override.self_approval_forbidden</c> when the approver is
    /// also the proposer; 409 if the row is already past
    /// <c>Proposed</c>.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    [OverrideWriteGate]
    [ProducesResponseType(typeof(OverrideDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OverrideDto>> Approve(Guid id, CancellationToken ct)
    {
        var subject = ResolveSubjectId();
        if (subject is null) return Unauthorized();

        return Ok(await _service.ApproveAsync(id, subject, ct));
    }

    /// <summary>
    /// Revoke a <c>Proposed</c> or <c>Approved</c> override. Requires
    /// a non-empty <c>RevocationReason</c>; the reaper-driven
    /// <c>Expired</c> path goes through P5.3 instead.
    /// </summary>
    [HttpPost("{id:guid}/revoke")]
    [OverrideWriteGate]
    [ProducesResponseType(typeof(OverrideDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OverrideDto>> Revoke(
        Guid id,
        [FromBody] RevokeOverrideRequest request,
        CancellationToken ct)
    {
        var subject = ResolveSubjectId();
        if (subject is null) return Unauthorized();

        return Ok(await _service.RevokeAsync(id, request, subject, ct));
    }

    /// <summary>
    /// List overrides matching the optional filter. Returns rows in
    /// any state; use <c>state=Approved</c> for the live set or
    /// <c>GET /api/overrides/active</c> for the
    /// scope-narrowed-and-non-expired projection.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OverrideDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<OverrideDto>>> List(
        [FromQuery] OverrideState? state,
        [FromQuery] OverrideScopeKind? scopeKind,
        [FromQuery] string? scopeRef,
        [FromQuery] Guid? policyVersionId,
        CancellationToken ct)
    {
        var filter = new OverrideListFilter(state, scopeKind, scopeRef, policyVersionId);
        return Ok(await _service.ListAsync(filter, ct));
    }

    /// <summary>
    /// Fetch a single override by id. Returns 404 if the row does not
    /// exist; visibility is not state-gated (an Expired or Revoked
    /// row is still readable for audit).
    /// </summary>
    [HttpGet("{id:guid}", Name = nameof(Get))]
    [ProducesResponseType(typeof(OverrideDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OverrideDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Currently-effective overrides for the given (scopeKind, scopeRef)
    /// pair. Returns only rows where <c>State == Approved</c> AND
    /// <c>ExpiresAt &gt; now</c> — expired rows are excluded even if
    /// the reaper has not yet ticked. Consumed by P4.3 chain
    /// resolution and by Conductor at admission time.
    /// </summary>
    [HttpGet("active")]
    [ProducesResponseType(typeof(IReadOnlyList<OverrideDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<OverrideDto>>> Active(
        [FromQuery, BindRequired] OverrideScopeKind scopeKind,
        [FromQuery, BindRequired] string scopeRef,
        CancellationToken ct)
        => Ok(await _service.GetActiveAsync(scopeKind, scopeRef, ct));

    private string? ResolveSubjectId()
    {
        // Same posture as PolicyVersionsLifecycleController (#13): never
        // write a fallback subject id into the catalog. JwtBearer maps
        // `sub` to NameIdentifier; TestAuthHandler sets the Name claim.
        // [Authorize] should already have returned 401 before we get
        // here — this is the belt to the framework's braces.
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? User.Identity?.Name;
        return string.IsNullOrEmpty(subject) ? null : subject;
    }
}
