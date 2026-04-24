// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

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

    public PoliciesController(IPolicyService policies)
    {
        _policies = policies;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PolicyDto>>> List(
        [FromQuery] string? namePrefix,
        [FromQuery] string? scope,
        [FromQuery] string? enforcement,
        [FromQuery] string? severity,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken ct)
    {
        var query = new ListPoliciesQuery(
            NamePrefix: namePrefix,
            Scope: scope,
            Enforcement: enforcement,
            Severity: severity,
            Skip: skip ?? 0,
            Take: take ?? 100);

        var results = await _policies.ListPoliciesAsync(query, ct);
        return Ok(results);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PolicyDto>> Get(Guid id, CancellationToken ct)
    {
        var policy = await _policies.GetPolicyAsync(id, ct);
        return policy is null ? NotFound() : Ok(policy);
    }

    [HttpGet("by-name/{name}")]
    public async Task<ActionResult<PolicyDto>> GetByName(string name, CancellationToken ct)
    {
        var policy = await _policies.GetPolicyByNameAsync(name, ct);
        return policy is null ? NotFound() : Ok(policy);
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<IReadOnlyList<PolicyVersionDto>>> ListVersions(
        Guid id, CancellationToken ct)
    {
        var versions = await _policies.ListVersionsAsync(id, ct);
        return Ok(versions);
    }

    /// <summary>
    /// Resolves the active version per ADR 0001 (highest <c>Version</c> with
    /// <c>State != Draft</c> in P1; <c>State == Active</c> after P2 lands).
    /// Route literal "active" sits before <c>{versionId:guid}</c> in match precedence,
    /// and the GUID constraint makes the two routes unambiguous regardless.
    /// </summary>
    [HttpGet("{id:guid}/versions/active")]
    public async Task<ActionResult<PolicyVersionDto>> GetActiveVersion(
        Guid id, CancellationToken ct)
    {
        var active = await _policies.GetActiveVersionAsync(id, ct);
        return active is null ? NotFound() : Ok(active);
    }

    [HttpGet("{id:guid}/versions/{versionId:guid}")]
    public async Task<ActionResult<PolicyVersionDto>> GetVersion(
        Guid id, Guid versionId, CancellationToken ct)
    {
        var version = await _policies.GetVersionAsync(id, versionId, ct);
        return version is null ? NotFound() : Ok(version);
    }

    [HttpPost]
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
