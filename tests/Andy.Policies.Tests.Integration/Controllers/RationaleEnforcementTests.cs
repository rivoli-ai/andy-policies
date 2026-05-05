// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Infrastructure.Services;
using Andy.Settings.Client;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Andy.Policies.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// Integration coverage for P2.4 (#14) — rationale enforcement wired to
/// <c>andy.policies.rationaleRequired</c>. Stands up a parallel factory that
/// substitutes a controllable stub for <see cref="ISettingsSnapshot"/> so the
/// toggle state can be flipped from the test, then drives the lifecycle
/// publish endpoint end-to-end.
/// </summary>
public class RationaleEnforcementTests
{
    private sealed class ControllableSnapshot : ISettingsSnapshot
    {
        public bool? RationaleRequired { get; set; } = true;

        public bool? GetBool(string key) =>
            key == AndySettingsRationalePolicy.SettingKey ? RationaleRequired : null;

        public string? GetString(string key) => null;

        public int? GetInt(string key) => null;

        public IReadOnlyCollection<string> Keys => Array.Empty<string>();

        public DateTimeOffset? LastRefreshedAt => DateTimeOffset.UtcNow;
    }

    private sealed class RationaleFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public ControllableSnapshot Snapshot { get; } = new();

        public RationaleFactory()
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

                // Replace the snapshot registered by AddAndySettingsClient with our
                // controllable stub. The rationale policy injects ISettingsSnapshot
                // and reads it on every check, so flipping `Snapshot.RationaleRequired`
                // takes effect immediately without restarting the host.
                var snapshotDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(ISettingsSnapshot));
                if (snapshotDescriptor is not null) services.Remove(snapshotDescriptor);
                services.AddSingleton<ISettingsSnapshot>(Snapshot);

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
                services.PostConfigure<AuthorizationOptions>(opts =>
                {
                    opts.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .Build();
                });

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

    private static CreatePolicyRequest MinimalCreate(string name) => new(
        Name: name,
        Description: null,
        Summary: "summary",
        Enforcement: "Must",
        Severity: "Critical",
        Scopes: Array.Empty<string>(),
        RulesJson: "{}");

    private static async Task<PolicyVersionDto> CreateDraftAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/policies", MinimalCreate(name));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
    }

    [Fact]
    public async Task ToggleOn_EmptyRationale_Returns400_WithRationaleErrorsAndProblemType()
    {
        using var factory = new RationaleFactory { Snapshot = { RationaleRequired = true } };
        var client = factory.CreateClient();
        var draft = await CreateDraftAsync(client, "rationale-on");

        var response = await client.PostAsJsonAsApproverAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest(""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("type").GetString().Should().Be("/problems/rationale-required");
        doc.RootElement.GetProperty("title").GetString().Should().Be("Rationale required");
        doc.RootElement.GetProperty("errors").GetProperty("rationale")
            .EnumerateArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task ToggleOff_EmptyRationale_Returns200()
    {
        using var factory = new RationaleFactory { Snapshot = { RationaleRequired = false } };
        var client = factory.CreateClient();
        var draft = await CreateDraftAsync(client, "rationale-off");

        var response = await client.PostAsJsonAsApproverAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest(""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<PolicyVersionDto>();
        dto!.State.Should().Be("Active");
    }

    [Fact]
    public async Task RuntimeFlip_FromOnToOff_TakesEffectImmediately()
    {
        using var factory = new RationaleFactory { Snapshot = { RationaleRequired = true } };
        var client = factory.CreateClient();
        var draft = await CreateDraftAsync(client, "rationale-flip");

        // First attempt with toggle on + empty rationale → 400.
        var first = await client.PostAsJsonAsApproverAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest(""));
        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Operator flips the toggle in andy-settings — the next snapshot read
        // returns false. AndySettingsRationalePolicy reads ISettingsSnapshot
        // on every check, so the same client request now succeeds without a
        // restart.
        factory.Snapshot.RationaleRequired = false;
        var second = await client.PostAsJsonAsApproverAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest(""));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ColdStart_SnapshotMissing_FailsSafe_To400()
    {
        using var factory = new RationaleFactory { Snapshot = { RationaleRequired = null } };
        var client = factory.CreateClient();
        var draft = await CreateDraftAsync(client, "rationale-coldstart");

        var response = await client.PostAsJsonAsApproverAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest(""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
