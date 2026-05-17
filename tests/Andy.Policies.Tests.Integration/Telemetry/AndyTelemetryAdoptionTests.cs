// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Andy.Policies.Api.Telemetry;
using Andy.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Telemetry;

/// <summary>
/// OT5 (rivoli-ai/conductor#1263). Asserts the andy-policies service:
///   1. Calls <see cref="AndyTelemetryExtensions.AddAndyTelemetry"/> from the
///      shared library without throwing.
///   2. Registers the canonical <see cref="PoliciesTelemetry"/> + the
///      sub-component meters under the same shared library call site.
/// </summary>
public class AndyTelemetryAdoptionTests
{
    [Fact]
    public void AddAndyTelemetry_registers_policies_sources_and_meters()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AndyTelemetry:ServiceName"] = "andy-policies",
            })
            .Build();

        services.AddAndyTelemetry(configuration, o =>
        {
            o.ActivitySources.Add(PoliciesTelemetry.ActivitySourceName);
            o.Meters.Add(PoliciesTelemetry.MeterName);
            o.Meters.Add("Andy.Policies.OverrideExpiryReaper");
            o.Meters.Add("Andy.Policies.RbacChecker");
            o.EnableAspNetCoreInstrumentation = false;
            o.EnableHttpClientInstrumentation = true;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AndyTelemetryOptions>();

        Assert.Contains(PoliciesTelemetry.ActivitySourceName, options.ActivitySources);
        Assert.Contains(PoliciesTelemetry.MeterName, options.Meters);
        Assert.False(options.EnableAspNetCoreInstrumentation);
        Assert.True(options.EnableHttpClientInstrumentation);
    }

    [Fact]
    public void PoliciesActivitySource_emits_when_listened_to()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PoliciesTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        using (var activity = PoliciesTelemetry.ActivitySource.StartActivity("ResolvePolicy"))
        {
            Assert.NotNull(activity);
            activity!.SetTag("policy.kind", "BundlePinning");
        }

        Assert.Single(captured);
        Assert.Equal("ResolvePolicy", captured[0].OperationName);
        Assert.Equal("BundlePinning", captured[0].GetTagItem("policy.kind"));
    }
}
