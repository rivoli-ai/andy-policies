// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Andy.Policies.Tests.Integration.Fixtures;

/// <summary>
/// Shared stubs for the MCP-tool integration tests. The MCP tools take
/// <see cref="IRbacChecker"/> + <see cref="IHttpContextAccessor"/>
/// directly (P7.6 #64), so every test that drives a tool body must
/// supply both. These helpers keep the test bodies focused on the
/// MCP wire contract rather than restating the wiring per file.
/// </summary>
internal static class McpToolStubs
{
    /// <summary>An <see cref="IRbacChecker"/> that always allows.</summary>
    public static IRbacChecker AllowAllRbac { get; } = new AllowAllRbacChecker();

    /// <summary>An <see cref="IRbacChecker"/> that always denies with
    /// <c>"no-permission"</c>.</summary>
    public static IRbacChecker DenyAllRbac { get; } = new DenyAllRbacChecker();

    /// <summary>
    /// Build a static <see cref="IHttpContextAccessor"/> whose principal
    /// has the supplied subject id. Pass <c>null</c> for an
    /// unauthenticated principal (no NameIdentifier claim).
    /// </summary>
    public static IHttpContextAccessor AccessorFor(string? subjectId)
    {
        var ctx = new DefaultHttpContext();
        if (subjectId is not null)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, subjectId),
            }, authenticationType: "Test"));
        }
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private sealed class AllowAllRbacChecker : IRbacChecker
    {
        public Task<RbacDecision> CheckAsync(
            string subjectId, string permissionCode, IReadOnlyList<string> groups,
            string? resourceInstanceId, CancellationToken ct)
            => Task.FromResult(new RbacDecision(true, "test-allow"));
    }

    private sealed class DenyAllRbacChecker : IRbacChecker
    {
        public Task<RbacDecision> CheckAsync(
            string subjectId, string permissionCode, IReadOnlyList<string> groups,
            string? resourceInstanceId, CancellationToken ct)
            => Task.FromResult(new RbacDecision(false, "no-permission"));
    }
}
