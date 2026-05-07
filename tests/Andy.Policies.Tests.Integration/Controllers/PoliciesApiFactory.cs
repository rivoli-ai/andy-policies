// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Andy.Policies.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// Test factory that swaps the registered <see cref="AppDbContext"/> for a SQLite
/// in-memory backing store and runs <c>EnsureCreated</c> at startup. Avoids the
/// pre-existing <c>ItemsControllerTests</c> failure mode (default factory tries to
/// migrate against Postgres on :5439 because of the auto-migrate in <c>Program.cs</c>).
/// </summary>
public class PoliciesApiFactory : WebApplicationFactory<Program>
{
    // Connection lives for the lifetime of the factory; closing it would drop the
    // in-memory database. SQLite's :memory: store is per-connection, so a single
    // shared SqliteConnection underpins every DbContext scoped from DI.
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public PoliciesApiFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" environment skips the Development-gated auto-migrate block in
        // Program.cs (which would otherwise try Postgres on :5439).
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                // Production Program.cs throws if AndyAuth:Authority is empty (see #103
                // — there is no auth-bypass branch). Provide a placeholder so the JWT
                // Bearer registration runs without throwing; the scheme is never invoked
                // because ConfigureServices below installs TestAuthHandler as the default.
                ["AndyAuth:Authority"] = "https://test-auth.invalid",
                // Same posture as AndyAuth: Program.cs throws on missing
                // AndySettings:ApiBaseUrl (#108 — no silent dev bypass).
                // Test fixture supplies a placeholder; the registered HTTP
                // client never gets called because nothing in the controller
                // tests reads settings yet (consumers land in P2.4/P5.4/etc.).
                ["AndySettings:ApiBaseUrl"] = "https://test-settings.invalid",
                // Same posture for AndyRbac:BaseUrl: Program.cs throws if it's
                // missing (P7.2 #51 — no production fail-closed bypass). The
                // typed HttpClient is never actually called because the
                // ConfigureServices block below installs an inline allow-all
                // stub for IRbacChecker.
                ["AndyRbac:BaseUrl"] = "https://test-rbac.invalid",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Drop the production DbContext registration before re-adding ours.
            var ctxDescriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (ctxDescriptor is not null) services.Remove(ctxDescriptor);

            services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

            // Replace the auth-bypass scheme (which actually fails [Authorize] because
            // it has no default scheme) with a test handler that always issues a
            // principal. Production auth (JWT Bearer) is unchanged outside this factory.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(opts =>
            {
                opts.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .Build();
            });

            // Replace the production HttpRbacChecker (which would attempt a
            // 3s call to https://test-rbac.invalid and fail-closed deny on
            // every RBAC-gated path) with an inline allow-all. P7.4 (#57)
            // adds a separate WireMock-backed harness that exercises the
            // real adapter against stubbed andy-rbac responses.
            var rbacDescriptors = services
                .Where(d => d.ServiceType == typeof(IRbacChecker))
                .ToList();
            foreach (var d in rbacDescriptors) services.Remove(d);
            services.AddSingleton<IRbacChecker>(new AllowAllStubRbacChecker());

            // P8.4 (#84): the bundle-pinning gate defaults to `true` per
            // the manifest. The bulk of pre-P8.4 integration tests
            // exercise the live read paths (no `?bundleId=`); flipping
            // pinning ON inside the factory would 400 every one of
            // them. Stub IPinningPolicy with pinning OFF so the legacy
            // tests stay focused on what they were testing. The gate
            // itself gets exercised by BundlePinningGateTests via its
            // own factory variant that pins ON.
            var pinDescriptors = services
                .Where(d => d.ServiceType == typeof(Andy.Policies.Application.Interfaces.IPinningPolicy))
                .ToList();
            foreach (var d in pinDescriptors) services.Remove(d);
            services.AddSingleton<Andy.Policies.Application.Interfaces.IPinningPolicy>(
                new StaticPinningPolicy(required: false));

            // P9 follow-up #193 (rationale on draft mutations): the manifest
            // default for `andy.policies.rationaleRequired` is `true`. Now
            // that CreatePolicyRequest / UpdatePolicyVersionRequest /
            // CreateBindingRequest carry a `Rationale` field, the
            // RationaleRequiredFilter (P2.4) enforces it on every mutating
            // request when the gate is on. Most pre-existing integration
            // tests don't supply rationale and aren't testing the gate;
            // stub IRationalePolicy off so they stay focused on their
            // actual subject. RationaleEnforcementTests uses its own
            // RationaleFactory with the gate on for the dedicated
            // enforcement coverage.
            var rationaleDescriptors = services
                .Where(d => d.ServiceType == typeof(Andy.Policies.Application.Interfaces.IRationalePolicy))
                .ToList();
            foreach (var d in rationaleDescriptors) services.Remove(d);
            services.AddSingleton<Andy.Policies.Application.Interfaces.IRationalePolicy>(
                new StaticRationalePolicy(required: false));

            // Build the schema once on the shared connection.
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _connection.Dispose();
        base.Dispose(disposing);
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

    /// <summary>
    /// Static <see cref="Andy.Policies.Application.Interfaces.IRationalePolicy"/> for tests
    /// that don't exercise the gate themselves; <see cref="RationaleEnforcementTests"/>
    /// uses its own factory variant for tests that flip the value.
    /// </summary>
    internal sealed class StaticRationalePolicy : Andy.Policies.Application.Interfaces.IRationalePolicy
    {
        public StaticRationalePolicy(bool required) => IsRequired = required;
        public bool IsRequired { get; }
        public string? ValidateRationale(string? rationale)
            => IsRequired && string.IsNullOrWhiteSpace(rationale)
                ? "Rationale is required for this operation."
                : null;
    }

    /// <summary>
    /// Static <see cref="Andy.Policies.Application.Interfaces.IPinningPolicy"/> for tests
    /// that don't exercise the gate themselves; <see cref="BundlePinningGateTests"/> uses
    /// its own factory variant for tests that flip the value.
    /// </summary>
    internal sealed class StaticPinningPolicy : Andy.Policies.Application.Interfaces.IPinningPolicy
    {
        public StaticPinningPolicy(bool required) => IsPinningRequired = required;
        public bool IsPinningRequired { get; }
    }
}
