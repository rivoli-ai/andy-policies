// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Filters;
using Andy.Policies.Application.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Filters;

/// <summary>
/// P5.4 (#56) — exercises <see cref="OverrideWriteGateAttribute"/>'s
/// short-circuit behaviour with a constructed
/// <see cref="ActionExecutingContext"/>. Lives in the Integration
/// suite because the Tests.Unit project doesn't pull in
/// Microsoft.AspNetCore.Mvc types.
/// </summary>
public class OverrideWriteGateAttributeTests
{
    private sealed class StubGate : IExperimentalOverridesGate
    {
        public bool IsEnabled { get; set; }
    }

    private static (ActionExecutingContext context, StubGate gate, NextDelegateProbe probe) NewContext(bool isEnabled)
    {
        var gate = new StubGate { IsEnabled = isEnabled };
        var services = new ServiceCollection();
        services.AddSingleton<IExperimentalOverridesGate>(gate);
        var sp = services.BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Path = "/api/overrides";

        var actionContext = new ActionContext(
            http,
            new RouteData(),
            new ActionDescriptor());
        var execContext = new ActionExecutingContext(
            actionContext,
            filters: new List<IFilterMetadata>(),
            actionArguments: new Dictionary<string, object?>(),
            controller: new object());
        var probe = new NextDelegateProbe();
        return (execContext, gate, probe);
    }

    [Fact]
    public async Task OnActionExecutionAsync_GateEnabled_CallsNextOnce()
    {
        var attr = new OverrideWriteGateAttribute();
        var (ctx, _, probe) = NewContext(isEnabled: true);

        await attr.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(1);
        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task OnActionExecutionAsync_GateDisabled_ShortCircuitsWith403AndProblemDetails()
    {
        var attr = new OverrideWriteGateAttribute();
        var (ctx, _, probe) = NewContext(isEnabled: false);

        await attr.OnActionExecutionAsync(ctx, probe.Next);

        probe.CallCount.Should().Be(0);
        var result = ctx.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status403Forbidden);
        problem.Type.Should().Be("/problems/override-disabled");
        problem.Extensions.Should().ContainKey("errorCode")
            .WhoseValue.Should().Be(OverrideWriteGateAttribute.ErrorCode);
    }

    [Fact]
    public void ErrorCode_IsStableContractString()
    {
        // Surface parity (P5.5–P5.7): MCP and gRPC return the same
        // string in their error envelopes. Pin the constant so a
        // rename is a deliberate cross-surface contract change.
        OverrideWriteGateAttribute.ErrorCode.Should().Be("override.disabled");
    }

    private sealed class NextDelegateProbe
    {
        public int CallCount { get; private set; }

        public Task<ActionExecutedContext> Next()
        {
            CallCount++;
            // The filter doesn't inspect the returned ActionExecutedContext;
            // returning a placeholder keeps the contract simple.
            var http = new DefaultHttpContext();
            var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
            return Task.FromResult(new ActionExecutedContext(
                actionContext, new List<IFilterMetadata>(), controller: new object()));
        }
    }
}
