// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using System.Security.Claims;
using Andy.Policies.Api.Mcp;
using Andy.Policies.Api.Mcp.Authorization;
using Andy.Policies.Application.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Xunit;

namespace Andy.Policies.Tests.Integration.Mcp;

public sealed class McpRbacCoverageTests
{
    [Fact]
    public void EveryBusinessTool_HasAWellFormedRbacGuard()
    {
        var assembly = typeof(PolicyTools).Assembly;
        var toolTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .Where(t => t != typeof(HelpTools));

        var unguarded = toolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Select(m => new { Type = t, Method = m,
                    Guard = m.GetCustomAttribute<RbacGuardAttribute>() }))
            .Where(x => x.Guard is null
                || string.IsNullOrWhiteSpace(x.Guard.PermissionCode)
                || !x.Guard.PermissionCode.StartsWith("andy-policies:", StringComparison.Ordinal))
            .Select(x => $"{x.Type.Name}.{x.Method.Name}")
            .OrderBy(x => x)
            .ToList();

        unguarded.Should().BeEmpty(
            "every catalog/audit MCP tool must declare its permission; HelpTools is the sole public-content allowlist");
    }

    [Fact]
    public async Task RbacDependencyException_FailsClosedWithStableDenial()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("sub", "user:alice") }, "test")),
        };
        var accessor = new HttpContextAccessor { HttpContext = context };

        var denial = await McpRbacGuard.GetDenialAsync(
            new ThrowingRbac(), accessor, "andy-policies:audit:read",
            "policy.audit", null, CancellationToken.None);

        denial.Should().Be("policy.audit.forbidden: rbac-unavailable");
    }

    private sealed class ThrowingRbac : IRbacChecker
    {
        public Task<RbacDecision> CheckAsync(
            string subjectId,
            string permissionCode,
            IReadOnlyList<string> groups,
            string? resourceInstanceId,
            CancellationToken ct) => throw new HttpRequestException("dependency unavailable");
    }
}
