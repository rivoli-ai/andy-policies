// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.AspNetCore.Http;

namespace Andy.Policies.Api.Authorization;

/// <summary>
/// Best-effort lift of the resource-instance id from the current request
/// route values. Maps a permission code's resource-type prefix
/// (<c>policy:</c>, <c>binding:</c>, …) to the conventional route
/// parameter names used by the controllers and returns the canonical
/// <c>"{resourceType}:{routeValue}"</c> string. Returns <c>null</c> when
/// no candidate is present — the call then targets the application
/// scope rather than a specific instance.
/// </summary>
public static class RouteResourceResolver
{
    private static readonly (string TypePrefix, string ResourceType, string[] RouteKeys)[] Mappings =
    {
        ("andy-policies:policy:",   "policy",   new[] { "id", "policyId" }),
        ("andy-policies:binding:",  "binding",  new[] { "id", "bindingId" }),
        ("andy-policies:scope:",    "scope",    new[] { "id", "scopeId" }),
        ("andy-policies:override:", "override", new[] { "id", "overrideId" }),
        ("andy-policies:bundle:",   "bundle",   new[] { "id", "bundleId" }),
        ("andy-policies:audit:",    "audit",    new[] { "id", "eventId", "seq" }),
    };

    public static string? Resolve(HttpContext httpContext, string permissionCode)
    {
        foreach (var (typePrefix, resourceType, routeKeys) in Mappings)
        {
            if (!permissionCode.StartsWith(typePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (var key in routeKeys)
            {
                if (httpContext.Request.RouteValues.TryGetValue(key, out var value)
                    && value is not null)
                {
                    var v = value.ToString();
                    if (!string.IsNullOrEmpty(v))
                    {
                        return $"{resourceType}:{v}";
                    }
                }
            }
            return null;
        }
        return null;
    }
}
