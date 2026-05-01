// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Filters;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// REST surface for the catalog audit chain (P6.5+). This file
/// scaffolds the <c>verify</c> endpoint (P6.5, story
/// rivoli-ai/andy-policies#45); list / get / export endpoints
/// follow in P6.6+.
/// </summary>
/// <remarks>
/// <para>
/// <b>RBAC.</b> Per <c>config/rbac-seed.json</c> the audit
/// surface belongs to the <c>risk</c> role. Today the API uses
/// blanket <c>[Authorize]</c>; per-action permission gates
/// (<c>andy-policies:audit:verify</c>, etc.) wire in P7.2 (#51)
/// when the andy-rbac client lands. The seed has been pre-
/// populated so the migration is purely a controller annotation
/// at that point.
/// </para>
/// <para>
/// <b>Read-only.</b> All endpoints in this controller are GETs
/// — no mutations live here. The <see cref="SkipRationaleCheckAttribute"/>
/// is therefore unnecessary, but the rationale filter
/// short-circuits on GET anyway.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/audit")]
[Produces("application/json")]
[Tags("Audit")]
public sealed class AuditController : ControllerBase
{
    private readonly IAuditChain _chain;

    public AuditController(IAuditChain chain)
    {
        _chain = chain;
    }

    /// <summary>
    /// Verifies the catalog audit hash chain, optionally bounded
    /// to a <c>[fromSeq, toSeq]</c> range. Divergence is
    /// reported as <c>{ valid: false, firstDivergenceSeq: N }</c>
    /// in the body (HTTP 200) — divergence is a legitimate
    /// queryable state, not an HTTP error.
    /// </summary>
    /// <param name="fromSeq">Inclusive lower bound. Defaults to
    /// 1 when omitted.</param>
    /// <param name="toSeq">Inclusive upper bound. Defaults to
    /// MAX(seq) when omitted.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Verification result. <c>valid</c>
    /// may be true or false; <c>firstDivergenceSeq</c> is
    /// non-null only when <c>valid</c> is false.</response>
    /// <response code="400">When <c>fromSeq</c> &gt;
    /// <c>toSeq</c>, or either is &lt; 1.</response>
    /// <response code="401">Caller is unauthenticated.</response>
    /// <response code="403">Caller lacks the
    /// <c>andy-policies:audit:verify</c> permission (P7.2).</response>
    [HttpGet("verify")]
    [ProducesResponseType(typeof(ChainVerificationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ChainVerificationDto>> Verify(
        [FromQuery] long? fromSeq,
        [FromQuery] long? toSeq,
        CancellationToken ct)
    {
        if (fromSeq is < 1)
        {
            return BadRequestRange("fromSeq must be >= 1.");
        }
        if (toSeq is < 1)
        {
            return BadRequestRange("toSeq must be >= 1.");
        }
        if (fromSeq is { } f && toSeq is { } t && f > t)
        {
            return BadRequestRange($"fromSeq ({f}) must be <= toSeq ({t}).");
        }

        var result = await _chain.VerifyChainAsync(fromSeq, toSeq, ct);
        return Ok(new ChainVerificationDto(
            result.Valid,
            result.FirstDivergenceSeq,
            result.InspectedCount,
            result.LastSeq));
    }

    private ActionResult<ChainVerificationDto> BadRequestRange(string detail)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Invalid verification range",
            Detail = detail,
            Type = "/problems/audit-verify-range",
            Instance = HttpContext.Request.Path,
            Extensions = { ["errorCode"] = "audit.verify.invalid_range" },
        };
        return BadRequest(problem);
    }
}
