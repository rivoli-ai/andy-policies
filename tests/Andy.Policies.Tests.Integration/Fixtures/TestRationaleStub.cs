// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Policies.Tests.Integration.Fixtures;

/// <summary>
/// P9 follow-up #193 (rationale on draft create/update) made
/// <see cref="IRationalePolicy"/> matter for `POST /api/policies`,
/// `PUT /api/policies/{id}/versions/{vId}`, and `POST /api/bindings`.
/// The manifest default for <c>andy.policies.rationaleRequired</c> is
/// <c>true</c>; tests that don't supply rationale on those mutations
/// previously skipped the filter (no Rationale field) but now hit the
/// gate. Tests that aren't testing the gate flip it off via this stub.
/// <see cref="Controllers.RationaleEnforcementTests"/> uses its own
/// snapshot-driven factory for the dedicated enforcement coverage.
/// </summary>
internal static class TestRationaleStub
{
    public static IServiceCollection StubRationaleOff(this IServiceCollection services)
        => Replace(services, required: false);

    public static IServiceCollection StubRationaleOn(this IServiceCollection services)
        => Replace(services, required: true);

    private static IServiceCollection Replace(IServiceCollection services, bool required)
    {
        var existing = services
            .Where(d => d.ServiceType == typeof(IRationalePolicy))
            .ToList();
        foreach (var d in existing) services.Remove(d);
        services.AddSingleton<IRationalePolicy>(new StaticRationalePolicy(required));
        return services;
    }

    /// <summary>
    /// Returns a derivative factory with the rationale gate flipped ON.
    /// Used by the few tests that specifically exercise empty-rationale
    /// rejection on publish/transition (where the parent factory has the
    /// gate OFF for the bulk of unrelated mutating tests).
    /// </summary>
    public static WebApplicationFactory<TEntry> WithRationaleOn<TEntry>(
        this WebApplicationFactory<TEntry> factory)
        where TEntry : class
        => factory.WithWebHostBuilder(b =>
            b.ConfigureServices(services => services.StubRationaleOn()));

    private sealed class StaticRationalePolicy : IRationalePolicy
    {
        public StaticRationalePolicy(bool required) => IsRequired = required;
        public bool IsRequired { get; }
        public string? ValidateRationale(string? rationale)
            => IsRequired && string.IsNullOrWhiteSpace(rationale)
                ? "Rationale is required for this operation."
                : null;
    }
}
