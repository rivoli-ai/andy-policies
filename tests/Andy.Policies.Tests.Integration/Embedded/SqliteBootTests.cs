// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Embedded;

/// <summary>
/// P10.1 (#31): boot smoke for the SQLite-embedded profile. Spins
/// up <see cref="WebApplicationFactory{Program}"/> with
/// <c>Database:Provider=Sqlite</c> + a fresh temp file, hits
/// <c>/health</c>, lists policies, and asserts the boot-time stock
/// seeder (P1.3 #73) populated the catalog with the six canonical
/// drafts. Acceptance test for the embedded-mode happy path.
/// </summary>
public class SqliteBootTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private EmbeddedFactory _factory = null!;

    public SqliteBootTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"andy-policies-embedded-{Guid.NewGuid():N}.db");
    }

    public Task InitializeAsync()
    {
        _factory = new EmbeddedFactory(_dbPath);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Health_AfterBoot_Returns200()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "embedded mode boot must reach the health endpoint within the test " +
            "factory's default startup window — Conductor's compose healthcheck " +
            "depends on this");
    }

    [Fact]
    public async Task ListPolicies_AfterBoot_ReturnsSeededStockCatalog()
    {
        using var client = _factory.CreateClient();
        // The list endpoint is gated by the bundle pinning gate
        // (P8.4 #84) when pinning is required. The embedded factory
        // stubs IPinningPolicy with required=false (matching the
        // standard test factory's posture) so the live list path
        // returns the seeded catalog directly.

        var resp = await client.GetAsync("/api/policies");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var rows = JsonDocument.Parse(body).RootElement.EnumerateArray().ToList();
        rows.Should().NotBeEmpty(
            "the boot-time stock-policy seeder (P1.3) populated the catalog; " +
            "GET /api/policies must surface those rows on a fresh embedded " +
            "boot — that's the P10.1 smoke contract.");
        rows.Should().HaveCountGreaterThanOrEqualTo(
            6,
            "there are six canonical stock policies; the seeder must land them " +
            "all on a fresh database. A short read points at a partial seed.");
    }

    [Fact]
    public async Task SecondBoot_AgainstSamePersistentDb_DoesNotReseed()
    {
        // Boot once to seed.
        using (var firstClient = _factory.CreateClient())
        {
            (await firstClient.GetAsync("/health")).EnsureSuccessStatusCode();
        }
        var initialCount = await CountPoliciesAsync();

        // Tear down + boot a second factory pointing at the same
        // physical SQLite file. Idempotent seeding means no
        // duplicates land — operators restarting a Conductor
        // container would see double-seeded rows otherwise.
        _factory.Dispose();
        using var secondFactory = new EmbeddedFactory(_dbPath);
        using (var secondClient = secondFactory.CreateClient())
        {
            (await secondClient.GetAsync("/health")).EnsureSuccessStatusCode();
        }

        var afterCount = await CountPoliciesAsync(secondFactory);
        afterCount.Should().Be(
            initialCount,
            "PolicySeeder uses presence-of-any-row as the idempotency probe " +
            "(see PolicySeeder.SeedStockPoliciesAsync); restart booting the " +
            "embedded image must NOT re-seed.");
    }

    private async Task<int> CountPoliciesAsync(EmbeddedFactory? factory = null)
    {
        factory ??= _factory;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Policies.AsNoTracking().CountAsync();
    }

    /// <summary>
    /// Boot the API exactly the way docker-compose.embedded.yml does:
    /// <c>Database:Provider=Sqlite</c> + a connection string pointing
    /// at a real file on disk (so subsequent boots see persisted
    /// state). Mirrors the relevant bits of <see cref="PoliciesApiFactory"/>
    /// — auth handler stubbed, RBAC stubbed allow-all, pinning gate
    /// stubbed off — but uses Production-style migrations
    /// (<c>db.Database.Migrate()</c>) instead of <c>EnsureCreated</c>
    /// so the boot path matches what Conductor operators get.
    /// </summary>
    private sealed class EmbeddedFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath;

        public EmbeddedFactory(string dbPath)
        {
            _dbPath = dbPath;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development env so Program.cs's MigrateAsync block runs
            // (P10.1 — the embedded-mode boot path).
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}",
                    ["AndyAuth:Authority"] = "https://test-auth.invalid",
                    ["AndySettings:ApiBaseUrl"] = "https://test-settings.invalid",
                    ["AndyRbac:BaseUrl"] = "https://test-rbac.invalid",
                });
            });

            builder.ConfigureServices(services =>
            {
                // The default AddAppDatabase(...) registration ran
                // before our ConfigureAppConfiguration override took
                // effect (it reads Database:Provider from
                // builder.Configuration during Program.cs ordering).
                // Strip it and re-register against our temp file —
                // same posture as PoliciesApiFactory.
                var ctxDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (ctxDescriptor is not null) services.Remove(ctxDescriptor);
                services.AddDbContext<AppDbContext>(opts =>
                    opts.UseSqlite($"Data Source={_dbPath}"));

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
                services.PostConfigure<AuthorizationOptions>(opts =>
                {
                    opts.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .Build();
                });

                // Stub the RBAC checker to allow everything — embedded
                // mode boots without a live andy-rbac in this test.
                var rbacDescriptors = services
                    .Where(d => d.ServiceType == typeof(Andy.Policies.Application.Interfaces.IRbacChecker))
                    .ToList();
                foreach (var d in rbacDescriptors) services.Remove(d);
                services.AddSingleton<Andy.Policies.Application.Interfaces.IRbacChecker, AllowAllRbacChecker>();

                // Stub the pinning gate off so /api/policies returns
                // live state without ?bundleId= (matches the standard
                // test factory's posture from P8.4).
                var pinDescriptors = services
                    .Where(d => d.ServiceType == typeof(Andy.Policies.Application.Interfaces.IPinningPolicy))
                    .ToList();
                foreach (var d in pinDescriptors) services.Remove(d);
                services.AddSingleton<Andy.Policies.Application.Interfaces.IPinningPolicy>(
                    new PoliciesApiFactory.StaticPinningPolicy(required: false));
            });
        }

        private sealed class AllowAllRbacChecker : Andy.Policies.Application.Interfaces.IRbacChecker
        {
            public Task<Andy.Policies.Application.Interfaces.RbacDecision> CheckAsync(
                string subjectId, string permissionCode, IReadOnlyList<string> groups,
                string? resourceInstanceId, CancellationToken ct)
                => Task.FromResult(new Andy.Policies.Application.Interfaces.RbacDecision(true, "embedded-test"));
        }
    }
}
