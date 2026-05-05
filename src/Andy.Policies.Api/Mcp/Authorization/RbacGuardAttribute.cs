// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Api.Mcp.Authorization;

/// <summary>
/// Declarative marker for MCP tool methods naming the
/// <c>andy-policies:…</c> permission code that the tool requires.
/// Coupled with <see cref="McpRbacGuard.EnsureAsync"/> the MCP tool
/// body invokes at the top — the attribute itself does not enforce;
/// it pins the contract for code review and reflective coverage tests.
/// P7.6 (#64).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RbacGuardAttribute : Attribute
{
    public string PermissionCode { get; }

    public RbacGuardAttribute(string permissionCode)
    {
        PermissionCode = permissionCode;
    }
}
