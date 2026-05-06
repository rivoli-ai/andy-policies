// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using Andy.Policies.Application.Manifest;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Manifest;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Andy.Policies.Tests.Integration.Manifest;

/// <summary>
/// P10.3 (#38): boots the API with manifest auto-registration enabled
/// and asserts the hosted service POSTs each block to its consumer
/// endpoint exactly once. Uses WireMock-backed stubs so the real typed
/// <see cref="System.Net.Http.HttpClient"/> path runs end to end —
/// failure modes (non-2xx, transport) bubble up the same way they
/// would against live andy-auth / andy-rbac / andy-settings.
/// </summary>
public class ManifestRegistrationIntegrationTests
{
    [Fact]
    public async Task AutoRegisterEnabled_HappyPath_DispatchesEachBlockOnce()
    {
        using var auth = WireMockServer.Start();
        using var rbac = WireMockServer.Start();
        using var settings = WireMockServer.Start();
        StubAccept(auth);
        StubAccept(rbac);
        StubAccept(settings);

        await using var factory = new ManifestFactory(autoRegister: true,
            authUrl: auth.Url + "/api/manifest",
            rbacUrl: rbac.Url + "/api/manifest",
            settingsUrl: settings.Url + "/api/manifest");

        // Triggers host start → hosted service runs.
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        Posts(auth, "/api/manifest").Should().Be(1, "auth block must be POSTed exactly once");
        Posts(rbac, "/api/manifest").Should().Be(1, "rbac block must be POSTed exactly once");
        Posts(settings, "/api/manifest").Should().Be(1, "settings block must be POSTed exactly once");
    }

    [Fact]
    public async Task AutoRegisterDisabled_NoEndpointsHit_HostStartsNormally()
    {
        using var auth = WireMockServer.Start();
        using var rbac = WireMockServer.Start();
        using var settings = WireMockServer.Start();
        StubAccept(auth);
        StubAccept(rbac);
        StubAccept(settings);

        await using var factory = new ManifestFactory(autoRegister: false,
            authUrl: auth.Url + "/api/manifest",
            rbacUrl: rbac.Url + "/api/manifest",
            settingsUrl: settings.Url + "/api/manifest");

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        Posts(auth, "/api/manifest").Should().Be(0);
        Posts(rbac, "/api/manifest").Should().Be(0);
        Posts(settings, "/api/manifest").Should().Be(0);
    }

    [Fact]
    public async Task AuthEndpointReturns500_HostStartupFailsLoud()
    {
        using var auth = WireMockServer.Start();
        using var rbac = WireMockServer.Start();
        using var settings = WireMockServer.Start();
        auth.Given(Request.Create().WithPath("/api/manifest").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("auth-broken"));
        StubAccept(rbac);
        StubAccept(settings);

        await using var factory = new ManifestFactory(autoRegister: true,
            authUrl: auth.Url + "/api/manifest",
            rbacUrl: rbac.Url + "/api/manifest",
            settingsUrl: settings.Url + "/api/manifest");

        var act = () => factory.CreateClient();
        // The hosted service throws ManifestRegistrationException on
        // 500; the host's startup wrapping surfaces it as the inner
        // exception of the failed StartAsync.
        act.Should().Throw<Exception>().Where(e =>
            e is ManifestRegistrationException
            || e.InnerException is ManifestRegistrationException
            || ContainsManifestRegistrationException(e));

        Posts(rbac, "/api/manifest").Should().Be(0,
            "rbac dispatch must not run after auth fails");
        Posts(settings, "/api/manifest").Should().Be(0,
            "settings dispatch must not run after auth fails");
    }

    [Fact]
    public async Task ReplayBoot_AgainstSameStubs_BothBootsSucceed()
    {
        // Idempotency on our side: dispatching the same manifest
        // twice in succession should both succeed. (Real consumer
        // upsert semantics live in the consumer repos; this test
        // proves we don't carry hidden once-only state in the hosted
        // service.)
        using var auth = WireMockServer.Start();
        using var rbac = WireMockServer.Start();
        using var settings = WireMockServer.Start();
        StubAccept(auth);
        StubAccept(rbac);
        StubAccept(settings);

        await using (var first = new ManifestFactory(autoRegister: true,
            authUrl: auth.Url + "/api/manifest",
            rbacUrl: rbac.Url + "/api/manifest",
            settingsUrl: settings.Url + "/api/manifest"))
        {
            (await first.CreateClient().GetAsync("/health")).EnsureSuccessStatusCode();
        }

        await using (var second = new ManifestFactory(autoRegister: true,
            authUrl: auth.Url + "/api/manifest",
            rbacUrl: rbac.Url + "/api/manifest",
            settingsUrl: settings.Url + "/api/manifest"))
        {
            (await second.CreateClient().GetAsync("/health")).EnsureSuccessStatusCode();
        }

        Posts(auth, "/api/manifest").Should().Be(2);
        Posts(rbac, "/api/manifest").Should().Be(2);
        Posts(settings, "/api/manifest").Should().Be(2);
    }

    private static void StubAccept(WireMockServer server)
        => server.Given(Request.Create().WithPath("/api/manifest").UsingPost())
                 .RespondWith(Response.Create().WithStatusCode(204));

    private static int Posts(WireMockServer server, string path)
        => server.LogEntries
            .Count(e => e.RequestMessage.Path == path
                        && string.Equals(e.RequestMessage.Method, "POST", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsManifestRegistrationException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is ManifestRegistrationException) return true;
        }
        return false;
    }

    /// <summary>
    /// WebApplicationFactory variant that wires the manifest hosted
    /// service against caller-supplied endpoint URLs. Anchors content
    /// root at the repo so <see cref="FileManifestLoader"/> finds the
    /// real <c>config/registration.json</c>.
    /// </summary>
    private sealed class ManifestFactory : WebApplicationFactory<Program>
    {
        private readonly bool _autoRegister;
        private readonly string _authUrl;
        private readonly string _rbacUrl;
        private readonly string _settingsUrl;
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public ManifestFactory(bool autoRegister, string authUrl, string rbacUrl, string settingsUrl)
        {
            _autoRegister = autoRegister;
            _authUrl = authUrl;
            _rbacUrl = rbacUrl;
            _settingsUrl = settingsUrl;
            _connection.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing env skips Program.cs's environment-gated
            // MigrateAsync block (PoliciesApiFactory uses the same
            // posture); the manifest hosted service runs regardless
            // of environment, gated only by Registration:AutoRegister.
            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["AndyAuth:Authority"] = "https://test-auth.invalid",
                    ["AndySettings:ApiBaseUrl"] = "https://test-settings.invalid",
                    ["AndyRbac:BaseUrl"] = "https://test-rbac.invalid",
                    ["Registration:AutoRegister"] = _autoRegister ? "true" : "false",
                    ["AndyAuth:ManifestEndpoint"] = _authUrl,
                    ["AndyRbac:ManifestEndpoint"] = _rbacUrl,
                    ["AndySettings:ManifestEndpoint"] = _settingsUrl,
                });
            });

            builder.ConfigureServices(services =>
            {
                var ctxDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (ctxDescriptor is not null) services.Remove(ctxDescriptor);
                services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
                services.PostConfigure<AuthorizationOptions>(opts =>
                {
                    opts.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .Build();
                });

                // The manifest test doesn't exercise the rbac path
                // through controllers; allow-all is fine.
                var rbacDescriptors = services
                    .Where(d => d.ServiceType == typeof(Andy.Policies.Application.Interfaces.IRbacChecker))
                    .ToList();
                foreach (var d in rbacDescriptors) services.Remove(d);
                services.AddSingleton<Andy.Policies.Application.Interfaces.IRbacChecker, AllowAllStubRbacChecker>();

                var pinDescriptors = services
                    .Where(d => d.ServiceType == typeof(Andy.Policies.Application.Interfaces.IPinningPolicy))
                    .ToList();
                foreach (var d in pinDescriptors) services.Remove(d);
                services.AddSingleton<Andy.Policies.Application.Interfaces.IPinningPolicy>(
                    new PoliciesApiFactory.StaticPinningPolicy(required: false));

                // The default WebApplicationFactory content root sits
                // at the API project, where config/registration.json
                // doesn't live. Replace the file-anchored loader with
                // one pointed directly at the repo's config dir.
                var loaderDescriptors = services
                    .Where(d => d.ServiceType == typeof(IManifestLoader))
                    .ToList();
                foreach (var d in loaderDescriptors) services.Remove(d);
                services.AddSingleton<IManifestLoader>(_ =>
                    new FileManifestLoader(Path.Combine(FindRepoRoot(), "config", "registration.json")));

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

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Andy.Policies.sln")))
            {
                dir = dir.Parent;
            }
            if (dir is null)
            {
                throw new InvalidOperationException(
                    "Could not locate andy-policies repo root from " + AppContext.BaseDirectory);
            }
            return dir.FullName;
        }

        private sealed class AllowAllStubRbacChecker : Andy.Policies.Application.Interfaces.IRbacChecker
        {
            public Task<Andy.Policies.Application.Interfaces.RbacDecision> CheckAsync(
                string subjectId, string permissionCode, IReadOnlyList<string> groups,
                string? resourceInstanceId, CancellationToken ct)
                => Task.FromResult(new Andy.Policies.Application.Interfaces.RbacDecision(true, "manifest-test"));
        }
    }
}
