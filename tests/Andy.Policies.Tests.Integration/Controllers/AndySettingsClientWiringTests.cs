// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Settings.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// Foundational tests for #108 — Andy.Settings.Client wiring. Asserts the
/// services registered by <c>AddAndySettingsClient</c> are resolvable from DI
/// inside the test host. Downstream consumers (P2.4 / P5.4 / P6.4 / P8.4) will
/// inject these directly; this fixture catches a broken registration before
/// those stories pick it up.
/// </summary>
public class AndySettingsClientWiringTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;

    public AndySettingsClientWiringTests(PoliciesApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void IAndySettingsClient_ResolvesFromDi()
    {
        using var scope = _factory.Services.CreateScope();
        var client = scope.ServiceProvider.GetService<IAndySettingsClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void ISettingsSnapshot_ResolvesFromDi()
    {
        using var scope = _factory.Services.CreateScope();
        var snapshot = scope.ServiceProvider.GetService<ISettingsSnapshot>();
        Assert.NotNull(snapshot);
    }
}
