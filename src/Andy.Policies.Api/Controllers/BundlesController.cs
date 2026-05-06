// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Application.Exceptions;
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
    private readonly IBundleDiffService _diff;
    private readonly IBundleService _bundles;

    public BundlesController(
        IBundleResolver resolver,
        IBundleDiffService diff,
        IBundleService bundles)
    {
        _resolver = resolver;
        _diff = diff;
        _bundles = bundles;
    }

    /// <summary>
    /// Create a new bundle (frozen snapshot of the live catalog).
    /// Returns 201 with the <see cref="BundleDto"/> + <c>Location</c>
    /// header pointing at <c>GET /api/bundles/{id}</c>.
    /// Maps <see cref="ValidationException"/> → 400 and
    /// <see cref="ConflictException"/> → 409 (duplicate active name).
    /// P8.6 (#86) — the CLI from this story is the first REST consumer.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "andy-policies:bundle:create")]
    [ProducesResponseType(typeof(BundleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BundleDto>> Create(
        [FromBody] CreateBundleRequest request,
        CancellationToken ct)
    {
        var actor = ResolveActor();
        if (actor is null) return Unauthorized();

        var dto = await _bundles.CreateAsync(request, actor, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>
    /// List bundles. <c>?includeDeleted=true</c> includes soft-deleted
    /// rows; <c>take</c> is clamped server-side. P8.6 (#86).
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "andy-policies:bundle:read")]
    [ProducesResponseType(typeof(IReadOnlyList<BundleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<BundleDto>>> List(
        [FromQuery] bool includeDeleted = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var rows = await _bundles.ListAsync(new ListBundlesFilter(includeDeleted, skip, take), ct);
        return Ok(rows);
    }

    /// <summary>
    /// Get a bundle by id. Returns 200 with <see cref="BundleDto"/> or
    /// 404 when the bundle is missing. Soft-deleted bundles are
    /// addressable here (the row remains for audit-chain integrity);
    /// pass <c>?includeDeleted=true</c> on <c>GET /api/bundles</c> to
    /// list them. P8.6 (#86).
    /// </summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = "andy-policies:bundle:read")]
    [ProducesResponseType(typeof(BundleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BundleDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _bundles.GetAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Soft-delete a bundle. State flips to
    /// <see cref="BundleState.Deleted"/>; the row remains in the table
    /// for audit-chain integrity. Idempotent: a second delete on the
    /// same id returns 404. P8.6 (#86).
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "andy-policies:bundle:delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromQuery] string rationale,
        CancellationToken ct)
    {
        var actor = ResolveActor();
        if (actor is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(rationale))
        {
            return ValidationProblem("rationale query parameter is required.");
        }

        var deleted = await _bundles.SoftDeleteAsync(id, actor, rationale, ct);
        return deleted ? NoContent() : NotFound();
    }

    private string? ResolveActor()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name;
        return string.IsNullOrEmpty(sub) ? null : sub;
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

    /// <summary>
    /// Emit an RFC-6902 JSON Patch between two bundles' frozen
    /// snapshots (P8.6, story rivoli-ai/andy-policies#86). Returns
    /// 200 with a <see cref="BundleDiffResult"/>; 404 when either
    /// bundle is missing or soft-deleted; 400 when the same id is
    /// passed for both <paramref name="id"/> and <c>to</c> (the
    /// trivial empty-patch case is allowed via two distinct ids
    /// that happen to resolve to identical canonical bytes — the
    /// diff returns <c>[]</c>; same-id is rejected as a likely
    /// caller mistake).
    /// </summary>
    [HttpGet("{id:guid}/diff")]
    [Authorize(Policy = "andy-policies:bundle:read")]
    [ProducesResponseType(typeof(BundleDiffResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Diff(
        Guid id,
        [FromQuery] Guid to,
        CancellationToken ct)
    {
        if (id == to)
        {
            return ValidationProblem(
                "Diff requires distinct from / to bundle ids; pass the same id only when " +
                "you actually want to diff a bundle against a different bundle whose " +
                "snapshot happens to be identical.");
        }

        var result = await _diff.DiffAsync(id, to, ct);
        if (result is null) return NotFound();
        return Ok(result);
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
