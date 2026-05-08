// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Security.Claims;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.Manifest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// #216 — caller-permission introspection (P9.3 prerequisite). Answers
/// the question the SPA needs to gate button visibility: "what
/// permissions does the current subject have on this service?"
/// </summary>
/// <remarks>
/// <para>
/// <b>Firewall posture (#103).</b> The browser must NOT reach
/// andy-rbac directly — that's an internal service. This controller
/// is the proxy: it resolves the catalog from the same registration
/// manifest that <c>tools/GenerateRbacDocs</c> consumes, asks
/// <see cref="IRbacChecker"/> (the same code path everything else
/// uses) for each permission, and returns the allow-set as a flat
/// string array. The cache TTL aligns with <c>HttpRbacChecker</c>'s
/// own 60s cache from P7.2 so a re-check inside the window costs at
/// most one in-process dictionary lookup.
/// </para>
/// <para>
/// <b>Why per-subject cache.</b> The check is a fan-out across N
/// permissions (today ~21). Without a cache, a button-heavy page
/// reload would N× the upstream rbac latency. With it, only the
/// first call within 60s pays.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    private readonly IManifestLoader _manifest;
    private readonly IRbacChecker _rbac;
    private readonly TimeProvider _clock;

    public AuthController(IManifestLoader manifest, IRbacChecker rbac, TimeProvider clock)
    {
        _manifest = manifest;
        _rbac = rbac;
        _clock = clock;
    }

    /// <summary>
    /// Return the permission codes the current authenticated subject is
    /// allowed to exercise on this service. Result is cached for 60s
    /// keyed on the subject id; flagged off the manifest's permission
    /// catalog so a new permission shows up in the response the moment
    /// it's added to <c>config/registration.json</c>.
    /// </summary>
    [HttpGet("permissions")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<string>>> Permissions(CancellationToken ct)
    {
        var subjectId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name;
        if (string.IsNullOrEmpty(subjectId))
        {
            // [Authorize] should have already returned 401; belt to the
            // framework's braces. See the lifecycle controller's actor-
            // fallback firewall (#13) for the same posture.
            return Unauthorized();
        }

        var now = _clock.GetUtcNow();
        if (Cache.TryGetValue(subjectId, out var hit) && hit.ExpiresAt > now)
        {
            return Ok(hit.Allowed);
        }

        var manifest = await _manifest.LoadAsync(ct).ConfigureAwait(false);
        var allowed = new List<string>(manifest.Rbac.Permissions.Count);

        // JWT groups claim — empty list until P7.4 plumbs it through;
        // matches the convention used by the override service's RBAC
        // calls (see OverrideService.RevokeAsync line 280).
        var groups = Array.Empty<string>();

        foreach (var perm in manifest.Rbac.Permissions)
        {
            // No resource instance — this endpoint answers the
            // application-level question ("can I publish at all?"),
            // not the per-resource one ("can I publish *this* policy
            // version?"). Per-resource refinement still happens at
            // the per-action enforcement point.
            var decision = await _rbac.CheckAsync(
                subjectId, perm.Code, groups, resourceInstanceId: null, ct)
                .ConfigureAwait(false);
            if (decision.Allowed)
            {
                allowed.Add(perm.Code);
            }
        }

        Cache[subjectId] = new CacheEntry(allowed, now + CacheTtl);
        return Ok(allowed);
    }

    private sealed record CacheEntry(IReadOnlyList<string> Allowed, DateTimeOffset ExpiresAt);
}
