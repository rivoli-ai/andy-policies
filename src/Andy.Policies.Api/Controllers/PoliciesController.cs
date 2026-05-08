// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Filters;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// REST surface for the policy catalog (P1.5, story rivoli-ai/andy-policies#75).
/// All endpoints delegate to <see cref="IPolicyService"/>; service exceptions are
/// mapped to status codes by <c>PolicyExceptionHandler</c> (registered globally).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PoliciesController : ControllerBase
{
    private readonly IPolicyService _policies;
    private readonly IBundleBackedPolicyReader _bundleReader;

    public PoliciesController(IPolicyService policies, IBundleBackedPolicyReader bundleReader)
    {
        _policies = policies;
        _bundleReader = bundleReader;
    }

    [HttpGet]
    [Authorize(Policy = "andy-policies:policy:read")]
    [RequiresBundlePin]
    public async Task<ActionResult<IReadOnlyList<PolicyDto>>> List(
        [FromQuery] string? namePrefix,
        [FromQuery] string? scope,
        [FromQuery] string? enforcement,
        [FromQuery] string? severity,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        [FromQuery] Guid? bundleId,
        CancellationToken ct)
    {
        var query = new ListPoliciesQuery(
            NamePrefix: namePrefix,
            Scope: scope,
            Enforcement: enforcement,
            Severity: severity,
            Skip: skip ?? 0,
            Take: take ?? 100);

        if (bundleId is { } pinned)
        {
            var pinnedResults = await _bundleReader.ListPoliciesAsync(pinned, query, ct);
            return pinnedResults is null ? NotFound() : Ok(pinnedResults);
        }
        var results = await _policies.ListPoliciesAsync(query, ct);
        return Ok(results);
    }

    /// <summary>
    /// #216 — approver inbox feed: Draft versions where
    /// <c>ReadyForReview = true</c>, ordered most-recently-created
    /// first. Authz on <c>:publish</c> rather than <c>:read</c> because
    /// only an approver should see this list; viewers stick to the
    /// regular per-policy version listing.
    /// </summary>
    [HttpGet("pending-approval")]
    [Authorize(Policy = "andy-policies:policy:publish")]
    [ProducesResponseType(typeof(IReadOnlyList<PolicyVersionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<PolicyVersionDto>>> ListPendingApproval(
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken ct)
    {
        var rows = await _policies.ListPendingApprovalAsync(
            skip ?? 0, take ?? 50, ct);
        return Ok(rows);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "andy-policies:policy:read")]
    [RequiresBundlePin]
    public async Task<ActionResult<PolicyDto>> Get(
        Guid id, [FromQuery] Guid? bundleId, CancellationToken ct)
    {
        if (bundleId is { } pinned)
        {
            var pinnedDto = await _bundleReader.GetPolicyAsync(pinned, id, ct);
            return pinnedDto is null ? NotFound() : Ok(pinnedDto);
        }
        var policy = await _policies.GetPolicyAsync(id, ct);
        return policy is null ? NotFound() : Ok(policy);
    }

    [HttpGet("by-name/{name}")]
    [Authorize(Policy = "andy-policies:policy:read")]
    [RequiresBundlePin]
    public async Task<ActionResult<PolicyDto>> GetByName(
        string name, [FromQuery] Guid? bundleId, CancellationToken ct)
    {
        if (bundleId is { } pinned)
        {
            var pinnedDto = await _bundleReader.GetPolicyByNameAsync(pinned, name, ct);
            return pinnedDto is null ? NotFound() : Ok(pinnedDto);
        }
        var policy = await _policies.GetPolicyByNameAsync(name, ct);
        return policy is null ? NotFound() : Ok(policy);
    }

    [HttpGet("{id:guid}/versions")]
    [Authorize(Policy = "andy-policies:policy:read")]
    [RequiresBundlePin]
    public async Task<ActionResult<IReadOnlyList<PolicyVersionDto>>> ListVersions(
        Guid id, [FromQuery] Guid? bundleId, CancellationToken ct)
    {
        if (bundleId is { } pinned)
        {
            var versions = await _bundleReader.ListVersionsAsync(pinned, id, ct);
            return versions is null ? NotFound() : Ok(versions);
        }
        var live = await _policies.ListVersionsAsync(id, ct);
        return Ok(live);
    }

    /// <summary>
    /// Resolves the active version per ADR 0001 (highest <c>Version</c> with
    /// <c>State != Draft</c> in P1; <c>State == Active</c> after P2 lands).
    /// Route literal "active" sits before <c>{versionId:guid}</c> in match precedence,
    /// and the GUID constraint makes the two routes unambiguous regardless.
    /// </summary>
    [HttpGet("{id:guid}/versions/active")]
    [Authorize(Policy = "andy-policies:policy:read")]
    [RequiresBundlePin]
    public async Task<ActionResult<PolicyVersionDto>> GetActiveVersion(
        Guid id, [FromQuery] Guid? bundleId, CancellationToken ct)
    {
        if (bundleId is { } pinned)
        {
            var pinnedActive = await _bundleReader.GetActiveVersionAsync(pinned, id, ct);
            return pinnedActive is null ? NotFound() : Ok(pinnedActive);
        }
        var active = await _policies.GetActiveVersionAsync(id, ct);
        return active is null ? NotFound() : Ok(active);
    }

    [HttpGet("{id:guid}/versions/{versionId:guid}")]
    [Authorize(Policy = "andy-policies:policy:read")]
    [RequiresBundlePin]
    public async Task<ActionResult<PolicyVersionDto>> GetVersion(
        Guid id, Guid versionId, [FromQuery] Guid? bundleId, CancellationToken ct)
    {
        if (bundleId is { } pinned)
        {
            var pinnedVersion = await _bundleReader.GetVersionAsync(pinned, id, versionId, ct);
            return pinnedVersion is null ? NotFound() : Ok(pinnedVersion);
        }
        var version = await _policies.GetVersionAsync(id, versionId, ct);
        return version is null ? NotFound() : Ok(version);
    }

    [HttpPost]
    [Authorize(Policy = "andy-policies:policy:author")]
    public async Task<ActionResult<PolicyVersionDto>> Create(
        [FromBody] CreatePolicyRequest request, CancellationToken ct)
    {
        var subjectId = User.Identity?.Name ?? "anonymous";
        var version = await _policies.CreateDraftAsync(request, subjectId, ct);
        return CreatedAtAction(
            nameof(GetVersion),
            new { id = version.PolicyId, versionId = version.Id },
            version);
    }

    [HttpPut("{id:guid}/versions/{versionId:guid}")]
    [Authorize(Policy = "andy-policies:policy:author")]
    public async Task<ActionResult<PolicyVersionDto>> UpdateDraft(
        Guid id,
        Guid versionId,
        [FromBody] UpdatePolicyVersionRequest request,
        CancellationToken ct)
    {
        var subjectId = User.Identity?.Name ?? "anonymous";
        var updated = await _policies.UpdateDraftAsync(id, versionId, request, subjectId, ct);
        return Ok(updated);
    }

    [HttpPost("{id:guid}/versions/{sourceVersionId:guid}/bump")]
    [Authorize(Policy = "andy-policies:policy:author")]
    public async Task<ActionResult<PolicyVersionDto>> Bump(
        Guid id, Guid sourceVersionId, CancellationToken ct)
    {
        var subjectId = User.Identity?.Name ?? "anonymous";
        var next = await _policies.BumpDraftFromVersionAsync(id, sourceVersionId, subjectId, ct);
        return CreatedAtAction(
            nameof(GetVersion),
            new { id = next.PolicyId, versionId = next.Id },
            next);
    }
}
