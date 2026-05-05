// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Api.Authorization;

/// <summary>
/// Centralised <see cref="IRbacChecker"/> bridge for ASP.NET's policy
/// authorization pipeline (P7.4, story rivoli-ai/andy-policies#57).
/// Pulls the subject id and groups from the validated
/// <see cref="ClaimsPrincipal"/> and the resource-instance id from the
/// current route, then delegates the decision to andy-rbac via
/// <c>HttpRbacChecker</c>.
/// </summary>
/// <remarks>
/// <para>
/// Per CLAUDE.md (#103) there is <b>no auth-bypass branch</b>. The
/// handler always calls <see cref="IRbacChecker"/>; tests substitute a
/// stub checker via DI. If the production andy-rbac is unreachable,
/// <c>HttpRbacChecker</c>'s fail-closed default surfaces here as
/// <see cref="RbacDecision.Allowed"/> = <c>false</c>, which the
/// pipeline then maps to HTTP 403.
/// </para>
/// <para>
/// Subject extraction tries
/// <see cref="ClaimTypes.NameIdentifier"/> (JWT <c>sub</c> after the
/// ASP.NET default claim mapping), then the raw <c>sub</c> claim (in
/// case mapping has been disabled), then
/// <see cref="System.Security.Principal.IIdentity.Name"/> (the
/// <see cref="ClaimTypes.Name"/> claim used by the integration test
/// auth scheme). This mirrors the controller-level fallback already
/// present at write sites such as
/// <see cref="Controllers.PolicyVersionsLifecycleController"/>.
/// </para>
/// </remarks>
public sealed class RbacAuthorizationHandler : AuthorizationHandler<RbacRequirement>
{
    private readonly IRbacChecker _rbac;
    private readonly IHttpContextAccessor _httpContext;
    private readonly ILogger<RbacAuthorizationHandler> _log;

    public RbacAuthorizationHandler(
        IRbacChecker rbac,
        IHttpContextAccessor httpContext,
        ILogger<RbacAuthorizationHandler> log)
    {
        _rbac = rbac;
        _httpContext = httpContext;
        _log = log;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RbacRequirement requirement)
    {
        var ctx = _httpContext.HttpContext;
        if (ctx is null)
        {
            return;
        }

        var subjectId = ResolveSubjectId(context.User);
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            // Authentication ran but no subject claim — let the
            // framework convert this into 403. (401 is the
            // authentication layer's job; we don't see unauthenticated
            // calls here.)
            return;
        }

        var groups = context.User.FindAll("groups").Select(c => c.Value).ToList();
        var resourceInstanceId = RouteResourceResolver.Resolve(ctx, requirement.PermissionCode);

        var decision = await _rbac.CheckAsync(
            subjectId,
            requirement.PermissionCode,
            groups,
            resourceInstanceId,
            ctx.RequestAborted).ConfigureAwait(false);

        if (decision.Allowed)
        {
            context.Succeed(requirement);
            return;
        }

        _log.LogInformation(
            "rbac deny subject={Subject} permission={Permission} instance={Instance} reason={Reason}",
            subjectId, requirement.PermissionCode, resourceInstanceId ?? "(none)", decision.Reason);
    }

    private static string? ResolveSubjectId(ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub")
        ?? user.Identity?.Name;
}
