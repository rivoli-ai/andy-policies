// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// Version-rooted enumeration of <c>Binding</c>s (P3.3, story
/// rivoli-ai/andy-policies#21). Sits next to the version resource so
/// HATEOAS-style clients can discover bindings without a separate
/// query. Delegates to <see cref="IBindingService"/>; the controller is
/// purely a wire-format adapter.
/// </summary>
[ApiController]
[Authorize]
[Route("api/policies/{policyId:guid}/versions/{versionId:guid}/bindings")]
[Produces("application/json")]
public sealed class PolicyVersionBindingsController : ControllerBase
{
    private readonly IBindingService _bindings;

    public PolicyVersionBindingsController(IBindingService bindings)
    {
        _bindings = bindings;
    }

    /// <summary>
    /// List bindings against the given <c>PolicyVersion</c>, ordered by
    /// most-recently-created first. <c>?includeDeleted=true</c> includes
    /// tombstoned rows; default <c>false</c> hides them.
    /// </summary>
    [HttpGet("")]
    [Authorize(Policy = "andy-policies:binding:read")]
    [ProducesResponseType(typeof(IReadOnlyList<BindingDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BindingDto>>> List(
        Guid policyId,
        Guid versionId,
        [FromQuery] bool includeDeleted,
        CancellationToken ct)
    {
        var results = await _bindings.ListByPolicyVersionAsync(versionId, includeDeleted, ct);
        return Ok(results);
    }
}
