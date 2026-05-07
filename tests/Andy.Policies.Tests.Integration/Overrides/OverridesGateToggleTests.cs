// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Tests.Integration.Fixtures;
using Andy.Policies.Application.Settings;
using Andy.Policies.Domain.Enums;
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
using Andy.Policies.Infrastructure.Data;
using Xunit;

namespace Andy.Policies.Tests.Integration.Overrides;

/// <summary>
/// P5.8 (#62) — exercises the live runtime toggle of
/// <c>andy.policies.experimentalOverridesEnabled</c>. The gate is a
/// pull-on-every-check primitive (no internal cache); production
/// hot-reload is the andy-settings refresh service. In-process tests
/// flip the same <see cref="IExperimentalOverridesGate"/> stub the
/// app resolves and assert the next request reflects the flip
/// without restarting the host.
/// </summary>
public class OverridesGateToggleTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class ToggleableGate : IExperimentalOverridesGate
    {
        public bool IsEnabled { get; set; }
    }

    private sealed class ToggleFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public ToggleableGate Gate { get; } = new();

        public ToggleFactory()
        {
            _connection.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["AndyAuth:Authority"] = "https://test-auth.invalid",
                    ["AndySettings:ApiBaseUrl"] = "https://test-settings.invalid",
                });
            });
            builder.ConfigureServices(services =>
            {
                var ctxDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (ctxDescriptor is not null) services.Remove(ctxDescriptor);
                services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

                var gateDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(IExperimentalOverridesGate));
                if (gateDescriptor is not null) services.Remove(gateDescriptor);
                services.AddSingleton<IExperimentalOverridesGate>(Gate);

                services.ReplaceWithAllowAll();

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
                services.PostConfigure<AuthorizationOptions>(opts =>
                {
                    opts.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .Build();
                });

                // P9 follow-up #193: rationale gate stubbed off — these tests
                // exercise the override write gate, not the rationale filter.
                services.StubRationaleOff();

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
    }

    private readonly ToggleFactory _factory = new();
    private HttpClient Client => _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<PolicyVersionDto> CreateActivePolicyVersionAsync(HttpClient client, string slug)
    {
        var resp = await client.PostAsJsonAsync("/api/policies", new CreatePolicyRequest(
            Name: slug,
            Description: null,
            Summary: "summary",
            Enforcement: "Must",
            Severity: "Critical",
            Scopes: Array.Empty<string>(),
            RulesJson: "{}"));
        resp.EnsureSuccessStatusCode();
        var draft = (await resp.Content.ReadFromJsonAsync<PolicyVersionDto>(JsonOptions))!;

        var publishResp = await client.PostAsJsonAsApproverAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest("go-live"));
        publishResp.EnsureSuccessStatusCode();
        return (await publishResp.Content.ReadFromJsonAsync<PolicyVersionDto>(JsonOptions))!;
    }

    private static ProposeOverrideRequest ExemptRequest(Guid pvid) => new(
        PolicyVersionId: pvid,
        ScopeKind: OverrideScopeKind.Principal,
        ScopeRef: "user:42",
        Effect: OverrideEffect.Exempt,
        ReplacementPolicyVersionId: null,
        ExpiresAt: DateTimeOffset.UtcNow.AddHours(24),
        Rationale: "expedite vendor-blocked story");

    [Fact]
    public async Task GateToggle_OffToOnToOff_ChangesWriteAuthorisationLive()
    {
        // Start with the gate off — propose returns 403 with the
        // structured override.disabled error code.
        _factory.Gate.IsEnabled = false;
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, "ovr-toggle");

        var firstAttempt = await client.PostAsJsonAsync("/api/overrides", ExemptRequest(version.Id));
        firstAttempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Flip the gate on — same client instance, no restart. The
        // next propose succeeds because the gate reads fresh on every
        // call (no internal cache).
        _factory.Gate.IsEnabled = true;
        var secondAttempt = await client.PostAsJsonAsync("/api/overrides", ExemptRequest(version.Id));
        secondAttempt.StatusCode.Should().Be(HttpStatusCode.Created);

        // Flip the gate back off — the next propose returns 403 again.
        _factory.Gate.IsEnabled = false;
        var thirdAttempt = await client.PostAsJsonAsync("/api/overrides", ExemptRequest(version.Id));
        thirdAttempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GateToggle_ReadsRemainAvailableThroughout()
    {
        // Reads bypass the gate so the resolution algorithm (P4.3)
        // and Conductor admission keep working when the toggle is
        // off. Verify GET endpoints return 200 in every gate state.
        _factory.Gate.IsEnabled = true;
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, "ovr-read");
        var proposeResp = await client.PostAsJsonAsync("/api/overrides", ExemptRequest(version.Id));
        proposeResp.EnsureSuccessStatusCode();

        foreach (var enabled in new[] { true, false, true, false })
        {
            _factory.Gate.IsEnabled = enabled;
            (await client.GetAsync("/api/overrides")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync(
                "/api/overrides/active?scopeKind=Principal&scopeRef=user:42"))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
