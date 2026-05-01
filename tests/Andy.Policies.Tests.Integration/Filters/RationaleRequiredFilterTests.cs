// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Filters;
using Andy.Policies.Application.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Filters;

/// <summary>
/// P6.4 (#44) — exercises <see cref="RationaleRequiredFilter"/>'s
/// short-circuit behaviour with constructed
/// <see cref="ActionExecutingContext"/>s. Lives in the Integration
/// suite because the Tests.Unit project doesn't pull in
/// Microsoft.AspNetCore.Mvc types.
/// </summary>
public class RationaleRequiredFilterTests
{
    private sealed class StubPolicy : IRationalePolicy
    {
        public bool IsRequired { get; set; } = true;

        public string? ValidateRationale(string? rationale) => null;
    }

    private sealed class DtoWithRationale
    {
        public string Rationale { get; set; } = string.Empty;
    }

    private sealed class DtoWithAttributedRationale
    {
        [Rationale]
        public string Reason { get; set; } = string.Empty;
    }

    private sealed class DtoWithoutRationale
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class FakeController
    {
        public void Mutate() { }
        [SkipRationaleCheck]
        public void SkipMe() { }
    }

    [SkipRationaleCheck]
    private sealed class FakeSkipController
    {
        public void AnyAction() { }
    }

    private static (ActionExecutingContext ctx, NextProbe probe) NewContext(
        string method, object? arg, IRationalePolicy policy, MethodInfo? actionMethod = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRationalePolicy>(policy);
        var sp = services.BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = method;
        http.Request.Path = "/api/policies/test";

        var actionContext = new ActionContext(
            http,
            new RouteData(),
            actionMethod is not null
                ? new ControllerActionDescriptor
                {
                    MethodInfo = actionMethod,
                    ControllerTypeInfo = actionMethod.DeclaringType!.GetTypeInfo(),
                }
                : new ActionDescriptor());
        var execContext = new ActionExecutingContext(
            actionContext,
            filters: new List<IFilterMetadata>(),
            actionArguments: arg is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["request"] = arg },
            controller: new object());
        var probe = new NextProbe();
        return (execContext, probe);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task ReadMethods_AlwaysPassThrough(string method)
    {
        var filter = new RationaleRequiredFilter();
        var (ctx, probe) = NewContext(method, new DtoWithRationale { Rationale = "" }, new StubPolicy());

        await filter.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(1);
        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task PolicyOff_PassesThrough_EvenWithEmptyRationale()
    {
        var filter = new RationaleRequiredFilter();
        var (ctx, probe) = NewContext(
            "POST",
            new DtoWithRationale { Rationale = "" },
            new StubPolicy { IsRequired = false });

        await filter.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(1);
        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task PolicyOn_NullRationale_ShortCircuitsWith400()
    {
        var filter = new RationaleRequiredFilter();
        var dto = new DtoWithRationale { Rationale = null! };
        var (ctx, probe) = NewContext("POST", dto, new StubPolicy { IsRequired = true });

        await filter.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(0);
        var result = ctx.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var problem = result.Value.Should().BeOfType<ValidationProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Type.Should().Be("/problems/rationale-required");
        problem.Extensions.Should().ContainKey("errorCode")
            .WhoseValue.Should().Be(RationaleRequiredFilter.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task PolicyOn_BlankRationale_ShortCircuitsWith400(string blank)
    {
        var filter = new RationaleRequiredFilter();
        var (ctx, probe) = NewContext(
            "POST",
            new DtoWithRationale { Rationale = blank },
            new StubPolicy { IsRequired = true });

        await filter.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(0);
        ctx.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PolicyOn_NonEmptyRationale_PassesThrough()
    {
        var filter = new RationaleRequiredFilter();
        var (ctx, probe) = NewContext(
            "POST",
            new DtoWithRationale { Rationale = "legal review complete" },
            new StubPolicy { IsRequired = true });

        await filter.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(1);
        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task PolicyOn_DtoWithoutRationaleField_PassesThrough()
    {
        // Some mutating endpoints (e.g. tombstone-by-id) don't
        // carry a rationale concept. The filter must pass them
        // through rather than fabricate a 400.
        var filter = new RationaleRequiredFilter();
        var (ctx, probe) = NewContext(
            "POST",
            new DtoWithoutRationale { Name = "x" },
            new StubPolicy { IsRequired = true });

        await filter.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(1);
        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task PolicyOn_AttributedRationaleProperty_IsDiscovered()
    {
        // [Rationale] on a non-canonical name must be honoured.
        var filter = new RationaleRequiredFilter();
        var (ctx, probe) = NewContext(
            "POST",
            new DtoWithAttributedRationale { Reason = "" },
            new StubPolicy { IsRequired = true });

        await filter.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(0);
        ctx.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SkipRationaleCheck_OnAction_ShortCircuitsTheFilter()
    {
        var filter = new RationaleRequiredFilter();
        var skipMethod = typeof(FakeController).GetMethod(nameof(FakeController.SkipMe))!;
        var (ctx, probe) = NewContext(
            "POST",
            new DtoWithRationale { Rationale = "" },
            new StubPolicy { IsRequired = true },
            actionMethod: skipMethod);

        await filter.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(1);
        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task SkipRationaleCheck_OnControllerType_ShortCircuitsTheFilter()
    {
        var filter = new RationaleRequiredFilter();
        var anyMethod = typeof(FakeSkipController).GetMethod(nameof(FakeSkipController.AnyAction))!;
        var (ctx, probe) = NewContext(
            "POST",
            new DtoWithRationale { Rationale = "" },
            new StubPolicy { IsRequired = true },
            actionMethod: anyMethod);

        await filter.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(1);
        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task PolicyMissingFromContainer_PassesThrough()
    {
        // If IRationalePolicy isn't registered (unlikely in
        // production, but keeps the filter robust on a partially-
        // configured test host), pass through rather than block
        // every mutating endpoint.
        var filter = new RationaleRequiredFilter();
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        http.Request.Method = "POST";
        var ctx = new ActionExecutingContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
        var probe = new NextProbe();

        await filter.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(1);
    }

    private sealed class NextProbe
    {
        public int CallCount { get; private set; }

        public Task<ActionExecutedContext> Next()
        {
            CallCount++;
            var http = new DefaultHttpContext();
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(http, new RouteData(), new ActionDescriptor()),
                new List<IFilterMetadata>(),
                controller: new object()));
        }
    }
}
