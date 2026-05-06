// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Filters;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// REST surface for the scope hierarchy (P4.5, story
/// rivoli-ai/andy-policies#33). Six endpoints sit on top of
/// <see cref="IScopeService"/> (P4.2) and
/// <see cref="IBindingResolutionService"/> (P4.3): list, get-by-id,
/// tree, effective-policies, create, delete. Service exceptions are
/// mapped to status codes by <c>PolicyExceptionHandler</c>:
/// <list type="bullet">
///   <item><see cref="Application.Exceptions.NotFoundException"/> → 404</item>
///   <item><see cref="Application.Exceptions.InvalidScopeTypeException"/> → 400 (<c>scope.parent-type-mismatch</c>)</item>
///   <item><see cref="Application.Exceptions.ScopeRefConflictException"/> → 409 (<c>scope.ref-conflict</c>)</item>
///   <item><see cref="Application.Exceptions.ScopeHasDescendantsException"/> → 409 (<c>scope.has-descendants</c>)</item>
/// </list>
/// </summary>
[ApiController]
[Authorize]
[Route("api/scopes")]
[Produces("application/json")]
public sealed class ScopesController : ControllerBase
{
    private readonly IScopeService _scopes;
    private readonly IBindingResolutionService _resolver;

    public ScopesController(IScopeService scopes, IBindingResolutionService resolver)
    {
        _scopes = scopes;
        _resolver = resolver;
    }

    /// <summary>
    /// List scope nodes. Optional <c>?type=</c> filter narrows to a
    /// single ScopeType (Org/Tenant/Team/Repo/Template/Run); without
    /// the filter the entire catalogue is returned ordered by
    /// (Depth, Ref).
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "andy-policies:scope:read")]
    [ProducesResponseType(typeof(IReadOnlyList<ScopeNodeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ScopeNodeDto>>> List(
        [FromQuery] ScopeType? type,
        CancellationToken ct)
    {
        var rows = await _scopes.ListAsync(type, ct);
        return Ok(rows);
    }

    /// <summary>
    /// Return the full forest as nested <see cref="ScopeTreeDto"/>s.
    /// One entry per root node; an empty catalogue returns an empty
    /// list. Order is (Ref ASC) at every level for deterministic
    /// snapshotting.
    /// </summary>
    [HttpGet("tree")]
    [Authorize(Policy = "andy-policies:scope:read")]
    [ProducesResponseType(typeof(IReadOnlyList<ScopeTreeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ScopeTreeDto>>> Tree(CancellationToken ct)
    {
        var forest = await _scopes.GetTreeAsync(ct);
        return Ok(forest);
    }

    /// <summary>
    /// Get a single scope node by id. Returns 404 if not found.
    /// </summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = "andy-policies:scope:read")]
    [ProducesResponseType(typeof(ScopeNodeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScopeNodeDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _scopes.GetAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Resolve the effective policy set for a scope node using the
    /// stricter-tightens-only fold (P4.3). Returns 404 if the node
    /// doesn't exist.
    /// </summary>
    [HttpGet("{id:guid}/effective-policies")]
    [Authorize(Policy = "andy-policies:scope:read")]
    [RequiresBundlePin]
    [ProducesResponseType(typeof(EffectivePolicySetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EffectivePolicySetDto>> Effective(
        Guid id,
        [FromQuery] Guid? bundleId,
        [FromServices] IBundleResolver bundleResolver,
        CancellationToken ct)
    {
        if (bundleId is { } pinned)
        {
            var snapshotResult = await bundleResolver.ResolveEffectiveForScopeAsync(pinned, id, ct);
            return snapshotResult is null ? NotFound() : Ok(snapshotResult);
        }
        var result = await _resolver.ResolveForScopeAsync(id, ct);
        return Ok(result);
    }

    /// <summary>
    /// Create a new scope node. <see cref="CreateScopeNodeRequest.ParentId"/>
    /// is null for a root <see cref="ScopeType.Org"/> node and
    /// required otherwise. The service enforces the canonical
    /// Org→Tenant→Team→Repo→Template→Run ladder; mismatched type
    /// returns 400.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "andy-policies:scope:manage")]
    [ProducesResponseType(typeof(ScopeNodeDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ScopeNodeDto>> Create(
        [FromBody] CreateScopeNodeRequest request,
        CancellationToken ct)
    {
        var dto = await _scopes.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>
    /// Delete a leaf scope node. Refuses with 409 if the node still
    /// has children; consumers must walk the subtree and delete
    /// leaves first.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "andy-policies:scope:manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _scopes.DeleteAsync(id, ct);
        return NoContent();
    }
}
