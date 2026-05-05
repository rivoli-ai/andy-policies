// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Andy.Policies.Tests.Integration.Authorization;

/// <summary>
/// P7.4 (#57) — pin the route-value → resource-instance mapping that
/// the <c>RbacAuthorizationHandler</c> attaches to every outgoing
/// <c>POST /api/check</c>.
/// </summary>
public class RouteResourceResolverTests
{
    private static HttpContext WithRoute(params (string Key, object Value)[] values)
    {
        var ctx = new DefaultHttpContext();
        foreach (var (key, value) in values)
        {
            ctx.Request.RouteValues[key] = value;
        }
        return ctx;
    }

    [Theory]
    [InlineData("andy-policies:policy:read", "id", "11111111-1111-1111-1111-111111111111", "policy:11111111-1111-1111-1111-111111111111")]
    [InlineData("andy-policies:policy:publish", "policyId", "abc", "policy:abc")]
    [InlineData("andy-policies:binding:read", "id", "b-42", "binding:b-42")]
    [InlineData("andy-policies:binding:manage", "bindingId", "b-7", "binding:b-7")]
    [InlineData("andy-policies:scope:manage", "id", "s-1", "scope:s-1")]
    [InlineData("andy-policies:override:approve", "id", "o-9", "override:o-9")]
    [InlineData("andy-policies:bundle:read", "bundleId", "b-3", "bundle:b-3")]
    [InlineData("andy-policies:audit:read", "id", "a-1", "audit:a-1")]
    public void Resolve_RouteWithMatchingKey_ReturnsTypedInstance(
        string permissionCode, string routeKey, string routeValue, string expected)
    {
        var ctx = WithRoute((routeKey, routeValue));

        RouteResourceResolver.Resolve(ctx, permissionCode).Should().Be(expected);
    }

    [Fact]
    public void Resolve_RouteWithNoMatchingKey_ReturnsNull()
    {
        var ctx = WithRoute(("unrelated", "x"));

        RouteResourceResolver.Resolve(ctx, "andy-policies:policy:publish").Should().BeNull();
    }

    [Fact]
    public void Resolve_EmptyRouteValues_ReturnsNull()
    {
        var ctx = new DefaultHttpContext();

        RouteResourceResolver.Resolve(ctx, "andy-policies:policy:read").Should().BeNull();
    }

    [Fact]
    public void Resolve_UnknownPermissionPrefix_ReturnsNull()
    {
        var ctx = WithRoute(("id", "x-1"));

        RouteResourceResolver.Resolve(ctx, "some-other-app:resource:read").Should().BeNull();
    }

    [Fact]
    public void Resolve_PrefersFirstCandidateRouteKey()
    {
        // Both `id` and `policyId` are candidates for policy codes; `id`
        // is listed first, so it wins.
        var ctx = WithRoute(("id", "first"), ("policyId", "second"));

        RouteResourceResolver.Resolve(ctx, "andy-policies:policy:read").Should().Be("policy:first");
    }
}
