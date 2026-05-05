// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// Helper for test factories that swap the production
/// <see cref="IRbacChecker"/> (an <c>HttpRbacChecker</c> pointed at a
/// placeholder URL) with an inline allow-all stub. Without this swap,
/// the controller-level <c>[Authorize(Policy = "andy-policies:…")]</c>
/// attributes added in P7.4 (#57) would fail-closed deny on every
/// request.
/// </summary>
internal static class TestRbacOverride
{
    public static IServiceCollection ReplaceWithAllowAll(this IServiceCollection services)
    {
        var existing = services
            .Where(d => d.ServiceType == typeof(IRbacChecker))
            .ToList();
        foreach (var d in existing) services.Remove(d);
        services.AddSingleton<IRbacChecker>(new AllowAllStubRbacChecker());
        return services;
    }

    private sealed class AllowAllStubRbacChecker : IRbacChecker
    {
        public Task<RbacDecision> CheckAsync(
            string subjectId,
            string permissionCode,
            IReadOnlyList<string> groups,
            string? resourceInstanceId,
            CancellationToken ct)
            => Task.FromResult(new RbacDecision(true, "test-allow"));
    }
}
