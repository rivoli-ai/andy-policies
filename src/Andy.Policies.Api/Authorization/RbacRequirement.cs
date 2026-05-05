// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.AspNetCore.Authorization;

namespace Andy.Policies.Api.Authorization;

/// <summary>
/// Authorization requirement that names a single permission code from
/// the P7.1 RBAC manifest (<c>config/registration.json</c>). One
/// requirement is registered per code so policy names round-trip via
/// the standard <c>[Authorize(Policy = "andy-policies:…")]</c>
/// attribute (P7.4, story rivoli-ai/andy-policies#57).
/// </summary>
public sealed class RbacRequirement(string permissionCode) : IAuthorizationRequirement
{
    public string PermissionCode { get; } = permissionCode;
}
