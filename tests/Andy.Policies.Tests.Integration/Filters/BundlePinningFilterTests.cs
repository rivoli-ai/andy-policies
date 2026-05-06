// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Filters;
using Andy.Policies.Application.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Andy.Policies.Tests.Unit.Filters;

/// <summary>
/// Unit tests for <see cref="BundlePinningFilter"/> (P8.4, story
/// rivoli-ai/andy-policies#84). Drives the filter against
/// hand-built <see cref="ActionExecutingContext"/> fixtures so the
/// gate logic is locked down independently of the HTTP / DI pipeline.
/// Pinning is applied per-action via
/// <see cref="RequiresBundlePinAttribute"/>; the filter must:
/// <list type="bullet">
///   <item>Pass through when the attribute is absent.</item>
///   <item>Pass through when <c>?bundleId=</c> is non-empty,
///     irrespective of pinning state.</item>
///   <item>Pass through when pinning is off.</item>
///   <item>Block with a 400 Problem Details when pinning is on
///     and <c>bundleId</c> is missing or empty.</item>
/// </list>
/// </summary>
public class BundlePinningFilterTests
{
    private sealed class StubPinning : IPinningPolicy
    {
        public StubPinning(bool required) => IsPinningRequired = required;
        public bool IsPinningRequired { get; }
    }

    private static ActionExecutingContext NewContext(
        bool hasAttr,
        IDictionary<string, Microsoft.Extensions.Primitives.StringValues>? query = null)
    {
        var http = new DefaultHttpContext();
        if (query is not null)
        {
            http.Request.QueryString = new QueryString(
                "?" + string.Join("&", query.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        var actionDescriptor = new ActionDescriptor
        {
            EndpointMetadata = hasAttr
                ? new List<object> { new RequiresBundlePinAttribute() }
                : new List<object>(),
        };
        var routeData = new RouteData();
        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(http, routeData, actionDescriptor);
        return new ActionExecutingContext(
            actionContext,
            filters: new List<IFilterMetadata>(),
            actionArguments: new Dictionary<string, object?>(),
            controller: new object());
    }

    private static Task<ActionExecutedContext> NoOpNext(ActionExecutingContext ctx) =>
        Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), ctx.Controller));

    [Fact]
    public async Task AttributeAbsent_PassesThrough_NoMatterPinningState()
    {
        var filter = new BundlePinningFilter(new StubPinning(required: true));
        var ctx = NewContext(hasAttr: false);
        var nextCalled = false;

        await filter.OnActionExecutionAsync(ctx, () =>
        {
            nextCalled = true;
            return NoOpNext(ctx);
        });

        nextCalled.Should().BeTrue();
        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task AttributePresent_BundleIdProvided_PassesThrough()
    {
        var filter = new BundlePinningFilter(new StubPinning(required: true));
        var ctx = NewContext(hasAttr: true,
            query: new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["bundleId"] = Guid.NewGuid().ToString(),
            });
        var nextCalled = false;

        await filter.OnActionExecutionAsync(ctx, () =>
        {
            nextCalled = true;
            return NoOpNext(ctx);
        });

        nextCalled.Should().BeTrue(
            "a non-empty bundleId is the consumer pinning what they want; the gate " +
            "doesn't care whether the id resolves — that's the action body's job");
    }

    [Fact]
    public async Task AttributePresent_BundleIdMissing_PinningOn_Returns400ProblemDetails()
    {
        var filter = new BundlePinningFilter(new StubPinning(required: true));
        var ctx = NewContext(hasAttr: true);
        var nextCalled = false;

        await filter.OnActionExecutionAsync(ctx, () =>
        {
            nextCalled = true;
            return NoOpNext(ctx);
        });

        nextCalled.Should().BeFalse("the gate must short-circuit before the action body runs");
        var result = ctx.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be(BundlePinningFilter.ProblemTypeUri);
        problem.Detail.Should().Contain("Pinning required: pass ?bundleId=");
    }

    [Fact]
    public async Task AttributePresent_BundleIdMissing_PinningOff_PassesThrough()
    {
        var filter = new BundlePinningFilter(new StubPinning(required: false));
        var ctx = NewContext(hasAttr: true);
        var nextCalled = false;

        await filter.OnActionExecutionAsync(ctx, () =>
        {
            nextCalled = true;
            return NoOpNext(ctx);
        });

        nextCalled.Should().BeTrue(
            "with pinning off, missing bundleId falls through to the live read path");
    }

    [Fact]
    public async Task AttributePresent_BundleIdEmpty_PinningOn_Returns400()
    {
        // ?bundleId= with no value (empty string) must be treated as
        // missing — otherwise a consumer could trivially bypass the
        // gate by appending the param without a value.
        var filter = new BundlePinningFilter(new StubPinning(required: true));
        var ctx = NewContext(hasAttr: true,
            query: new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["bundleId"] = "",
            });

        await filter.OnActionExecutionAsync(ctx, () => NoOpNext(ctx));

        var result = ctx.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
