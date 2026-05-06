// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Api.Filters;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// REST surface for <c>Binding</c> mutation, single-id read, and target-side
/// query (P3.3, story rivoli-ai/andy-policies#21). Delegates to
/// <see cref="IBindingService"/> from P3.2 — same service powering MCP
/// (P3.5), gRPC (P3.6), and CLI (P3.7). Service exceptions are mapped by
/// the global <c>PolicyExceptionHandler</c> (already covers
/// <see cref="Application.Exceptions.NotFoundException"/>,
/// <see cref="Application.Exceptions.ConflictException"/>, and
/// <see cref="Application.Exceptions.ValidationException"/>; the
/// <see cref="Application.Exceptions.BindingRetiredVersionException"/>
/// inherits from <see cref="Application.Exceptions.ConflictException"/> so
/// the existing 409 mapping catches it).
/// </summary>
[ApiController]
[Authorize]
[Route("api/bindings")]
[Produces("application/json")]
public sealed class BindingsController : ControllerBase
{
    private readonly IBindingService _bindings;

    public BindingsController(IBindingService bindings)
    {
        _bindings = bindings;
    }

    /// <summary>
    /// Create a new binding. Body: <see cref="CreateBindingRequest"/>.
    /// Returns 201 with a <c>Location</c> header pointing at the new
    /// resource. Refuses bindings to Retired versions with 409 Conflict;
    /// missing target version returns 404; oversized/empty
    /// <c>targetRef</c> returns 400.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "andy-policies:binding:manage")]
    [ProducesResponseType(typeof(BindingDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BindingDto>> Create(
        [FromBody] CreateBindingRequest request,
        CancellationToken ct)
    {
        var actor = ResolveActor();
        if (actor is null) return Unauthorized();

        var dto = await _bindings.CreateAsync(request, actor, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>
    /// Get a single binding by id. Returns 404 if the binding does not
    /// exist; tombstoned bindings are still visible here so audit
    /// investigators can inspect their <c>DeletedAt</c> stamp.
    /// </summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = "andy-policies:binding:read")]
    [ProducesResponseType(typeof(BindingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BindingDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _bindings.GetAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Soft-delete a binding. Returns 204 on success; calling delete on an
    /// already-tombstoned binding returns 404 (the row is treated as
    /// not-found). Accepts optional <c>?rationale=...</c> propagated to
    /// the audit record.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "andy-policies:binding:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromQuery] string? rationale,
        CancellationToken ct)
    {
        var actor = ResolveActor();
        if (actor is null) return Unauthorized();

        await _bindings.DeleteAsync(id, actor, rationale, ct);
        return NoContent();
    }

    /// <summary>
    /// List active bindings for a target — exact-equality match on
    /// <c>(targetType, targetRef)</c>, no prefix or case-folding (P3.2
    /// service contract). Tombstoned bindings are excluded; a separate
    /// version-rooted endpoint is available for inspection of deleted
    /// rows.
    /// </summary>
    [HttpGet("")]
    [Authorize(Policy = "andy-policies:binding:read")]
    [ProducesResponseType(typeof(IReadOnlyList<BindingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<BindingDto>>> Query(
        [FromQuery] BindingTargetType targetType,
        [FromQuery] string targetRef,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(targetRef))
        {
            return ValidationProblem("targetRef query parameter is required.");
        }
        var results = await _bindings.ListByTargetAsync(targetType, targetRef, ct);
        return Ok(results);
    }

    /// <summary>
    /// Resolve bindings for a target (P3.4, story
    /// rivoli-ai/andy-policies#22). Distinct from <see cref="Query"/>:
    /// joins each row to its <c>Policy</c> and <c>PolicyVersion</c> so
    /// callers get policy name, version state, enforcement, severity, and
    /// scopes without a second round-trip; filters out bindings whose
    /// target version is Retired; dedups same-target/same-version pairs
    /// preferring <c>Mandatory</c> over <c>Recommended</c>; orders the
    /// result deterministically (policy name ASC, then version number
    /// DESC). Exact-match only — no hierarchy walk; that's P4. An
    /// unknown target returns 200 with <c>count = 0</c>, never 404.
    /// </summary>
    [HttpGet("resolve")]
    [Authorize(Policy = "andy-policies:binding:read")]
    [RequiresBundlePin]
    [ProducesResponseType(typeof(ResolveBindingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ResolveBindingsResponse>> Resolve(
        [FromQuery] BindingTargetType targetType,
        [FromQuery] string targetRef,
        [FromQuery] Guid? bundleId,
        [FromServices] IBindingResolver resolver,
        [FromServices] IBundleResolver bundleResolver,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetRef))
        {
            return ValidationProblem("targetRef query parameter is required.");
        }

        if (bundleId is { } pinned)
        {
            // P8.4 (#84) — snapshot-backed dispatch. Translate the
            // BundleResolveResult shape to the existing
            // ResolveBindingsResponse so consumers see one wire shape
            // regardless of pinning. Missing/deleted bundle is a 404.
            var pinnedResult = await bundleResolver.ResolveAsync(pinned, targetType, targetRef, ct);
            if (pinnedResult is null) return NotFound();

            var dtos = pinnedResult.Bindings
                .Select(b => new ResolvedBindingDto(
                    BindingId: b.BindingId,
                    PolicyId: b.PolicyId,
                    PolicyName: b.PolicyName,
                    PolicyVersionId: b.PolicyVersionId,
                    VersionNumber: b.VersionNumber,
                    VersionState: LifecycleState.Active.ToString(),
                    Enforcement: b.Enforcement,
                    Severity: b.Severity,
                    Scopes: b.Scopes,
                    BindStrength: b.BindStrength))
                .ToList();
            return Ok(new ResolveBindingsResponse(targetType, targetRef, dtos, dtos.Count));
        }

        var response = await resolver.ResolveExactAsync(targetType, targetRef, ct);
        return Ok(response);
    }

    private string? ResolveActor()
    {
        // Mirrors the lifecycle controller's actor-fallback firewall (#13):
        // never write a fallback subject id into the catalog. JwtBearer
        // maps `sub` to NameIdentifier; TestAuthHandler sets the Name
        // claim. If neither is present, [Authorize] should already have
        // returned 401 — this is the belt to the framework's braces.
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name;
        return string.IsNullOrEmpty(sub) ? null : sub;
    }
}
