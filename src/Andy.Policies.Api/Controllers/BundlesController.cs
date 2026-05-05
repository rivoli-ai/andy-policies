// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// REST surface for resolving and reading from a frozen
/// <see cref="Andy.Policies.Domain.Entities.Bundle"/> snapshot
/// (P8.3, story rivoli-ai/andy-policies#83). Two endpoints today:
/// <list type="bullet">
///   <item><c>GET /api/bundles/{id}/resolve?targetType=&amp;targetRef=</c>
///     — bindings against the snapshot for a target.</item>
///   <item><c>GET /api/bundles/{id}/policies/{policyId}</c> — pinned
///     policy lookup.</item>
/// </list>
/// Mutation surfaces (POST / DELETE) land in P8.4–P8.5 alongside the
/// MCP and gRPC parity.
/// </summary>
/// <remarks>
/// <para>
/// <b>HTTP caching.</b> Bundles are immutable so responses carry
/// <c>ETag: "&lt;snapshotHash&gt;"</c> and
/// <c>Cache-Control: public, max-age=31536000, immutable</c>. A
/// matching <c>If-None-Match</c> short-circuits to 304. This is
/// transport-layer caching of an immutable artifact and does not
/// conflict with the epic's "no consumer caching" non-goal — that
/// rule governs application-level stale-data caches, not the HTTP
/// strong-validator path.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/bundles")]
[Produces("application/json")]
public sealed class BundlesController : ControllerBase
{
    private readonly IBundleResolver _resolver;

    public BundlesController(IBundleResolver resolver)
    {
        _resolver = resolver;
    }

    /// <summary>
    /// Resolve bindings for a <c>(targetType, targetRef)</c> pair
    /// against a frozen bundle. Exact-match only (mirrors the live
    /// <c>GET /api/bindings/resolve</c>); no hierarchy walk.
    /// Returns 200 with a <see cref="BundleResolveResult"/>; 404
    /// when the bundle does not exist or is soft-deleted; 400 when
    /// <paramref name="targetRef"/> is empty.
    /// </summary>
    [HttpGet("{id:guid}/resolve")]
    [Authorize(Policy = "andy-policies:bundle:read")]
    [ProducesResponseType(typeof(BundleResolveResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resolve(
        Guid id,
        [FromQuery] BindingTargetType targetType,
        [FromQuery] string targetRef,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetRef))
        {
            return ValidationProblem("targetRef query parameter is required.");
        }

        var result = await _resolver.ResolveAsync(id, targetType, targetRef, ct);
        if (result is null) return NotFound();

        if (TryReturnNotModified(result.SnapshotHash, out var notModified))
        {
            return notModified;
        }

        SetSnapshotCacheHeaders(result.SnapshotHash);
        return Ok(result);
    }

    /// <summary>
    /// Look up a single pinned policy by id. Returns 200 with a
    /// <see cref="BundlePinnedPolicyDto"/>, or 404 when the bundle
    /// does not exist / is soft-deleted, or the policy id is not in
    /// the snapshot.
    /// </summary>
    [HttpGet("{id:guid}/policies/{policyId:guid}")]
    [Authorize(Policy = "andy-policies:bundle:read")]
    [ProducesResponseType(typeof(BundlePinnedPolicyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPinnedPolicy(
        Guid id,
        Guid policyId,
        CancellationToken ct)
    {
        var dto = await _resolver.GetPinnedPolicyAsync(id, policyId, ct);
        if (dto is null) return NotFound();

        if (TryReturnNotModified(dto.SnapshotHash, out var notModified))
        {
            return notModified;
        }

        SetSnapshotCacheHeaders(dto.SnapshotHash);
        return Ok(dto);
    }

    private bool TryReturnNotModified(string snapshotHash, out IActionResult result)
    {
        var ifNoneMatch = Request.Headers[HeaderNames.IfNoneMatch].ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch))
        {
            // Strong validator: the ETag we emit is `"<hash>"`. Match
            // either form (with quotes from a compliant client, or
            // without from a permissive proxy).
            var bare = ifNoneMatch.Trim().Trim('"');
            if (string.Equals(bare, snapshotHash, StringComparison.Ordinal))
            {
                SetSnapshotCacheHeaders(snapshotHash);
                result = StatusCode(StatusCodes.Status304NotModified);
                return true;
            }
        }
        result = null!;
        return false;
    }

    private void SetSnapshotCacheHeaders(string snapshotHash)
    {
        Response.Headers[HeaderNames.ETag] = $"\"{snapshotHash}\"";
        // Bundles are immutable post-insert (P8.1's SaveChanges sweep),
        // so an aggressive max-age + immutable directive is honest:
        // the response body for a given (bundleId, snapshotHash) will
        // never change.
        Response.Headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
    }
}
