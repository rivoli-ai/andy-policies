// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Api.Authorization;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Andy.Policies.Api.Mcp.Authorization;

/// <summary>
/// Per-tool RBAC enforcement for MCP. The MCP framework
/// (<c>ModelContextProtocol</c>) does not surface a middleware seam
/// for tool dispatch, so each mutating tool calls
/// <see cref="EnsureAsync"/> at the top of its body. Same contract
/// as the REST <c>RbacAuthorizationHandler</c> and the gRPC
/// <c>RbacServerInterceptor</c>: subject + groups extracted from the
/// authenticated principal, decision delegated to
/// <see cref="IRbacChecker"/>, deny → throw. P7.6 (#64).
/// </summary>
public static class McpRbacGuard
{
    /// <summary>Convenience wrapper for text-returning tools. Returns
    /// null on allow or a stable prefixed denial string on deny.</summary>
    public static async Task<string?> GetDenialAsync(
        IRbacChecker rbac,
        IHttpContextAccessor httpContext,
        string permissionCode,
        string errorPrefix,
        string? resourceInstanceId,
        CancellationToken ct)
    {
        try
        {
            await EnsureAsync(rbac, httpContext, permissionCode, resourceInstanceId, ct)
                .ConfigureAwait(false);
            return null;
        }
        catch (McpAuthorizationException ex)
        {
            return $"{errorPrefix}.forbidden: {ex.Reason}";
        }
    }

    /// <summary>
    /// Throws <see cref="McpAuthorizationException"/> when the call
    /// must be denied. Returns silently on allow.
    /// </summary>
    public static async Task EnsureAsync(
        IRbacChecker rbac,
        IHttpContextAccessor httpContext,
        string permissionCode,
        string? resourceInstanceId,
        CancellationToken ct)
    {
        var ctx = httpContext.HttpContext
            ?? throw new McpAuthorizationException(
                permissionCode, "no-http-context",
                "MCP tool ran without an HTTP context — cannot extract caller identity.");

        var subjectId = ActorSubjectResolver.Resolve(ctx.User);
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            throw new McpAuthorizationException(
                permissionCode, "no-subject",
                "MCP caller is missing a subject claim.");
        }

        var groups = ctx.User.FindAll("groups").Select(c => c.Value).ToList();
        RbacDecision decision;
        try
        {
            decision = await rbac.CheckAsync(
                subjectId, permissionCode, groups, resourceInstanceId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpAuthorizationException(
                permissionCode,
                "rbac-unavailable",
                $"RBAC dependency failed closed: {ex.GetType().Name}.");
        }
        if (!decision.Allowed)
        {
            throw new McpAuthorizationException(
                permissionCode, decision.Reason,
                $"MCP caller '{subjectId}' is not permitted to '{permissionCode}'" +
                (resourceInstanceId is null ? "." : $" on '{resourceInstanceId}'.") +
                $" Reason: {decision.Reason}");
        }
    }
}

/// <summary>
/// Thrown by <see cref="McpRbacGuard.EnsureAsync"/> when the caller is
/// denied. MCP tools may catch this and translate to their typed
/// error string (e.g. <c>"policy.override.forbidden: …"</c>) so the
/// caller sees a structured tool result rather than an opaque server
/// failure.
/// </summary>
public sealed class McpAuthorizationException : Exception
{
    public string PermissionCode { get; }

    public string Reason { get; }

    public McpAuthorizationException(string permissionCode, string reason, string message)
        : base(message)
    {
        PermissionCode = permissionCode;
        Reason = reason;
    }
}
