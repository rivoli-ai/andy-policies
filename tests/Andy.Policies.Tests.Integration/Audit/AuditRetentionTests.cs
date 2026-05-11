// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Infrastructure.Data;
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
using Xunit;

namespace Andy.Policies.Tests.Integration.Audit;

/// <summary>
/// ADR 0006.1 (story rivoli-ai/andy-policies#110) — end-to-end
/// behavior of the audit-retention setting against the real REST
/// pipeline, with a fixed clock + an injected
/// <see cref="IAuditRetentionPolicy"/> stub so the test owns the
/// staleness threshold.
/// </summary>
public class AuditRetentionTests : IDisposable
{
    private sealed class StubRetentionPolicy : IAuditRetentionPolicy
    {
        public DateTimeOffset? Threshold { get; set; }
        public DateTimeOffset? GetStalenessThreshold(DateTimeOffset now) => Threshold;
    }

    private sealed class RetentionFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public RetentionFactory()
        {
            _connection.Open();
        }

        public StubRetentionPolicy Retention { get; } = new();
        public DateTimeOffset NowOverride { get; set; } =
            new(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);

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
                    ["AndyRbac:BaseUrl"] = "https://test-rbac.invalid",
                });
            });
            builder.ConfigureServices(services =>
            {
                var ctxDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (ctxDescriptor is not null) services.Remove(ctxDescriptor);
                services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

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

                // Replace the live AuditRetentionPolicy + clock with the
                // test stubs so each test owns the threshold.
                var rDescriptors = services
                    .Where(d => d.ServiceType == typeof(IAuditRetentionPolicy))
                    .ToList();
                foreach (var d in rDescriptors) services.Remove(d);
                services.AddSingleton<IAuditRetentionPolicy>(Retention);

                var clockDescriptors = services
                    .Where(d => d.ServiceType == typeof(TimeProvider))
                    .ToList();
                foreach (var d in clockDescriptors) services.Remove(d);
                services.AddSingleton<TimeProvider>(new FixedClock(() => NowOverride));

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

    private sealed class FixedClock : TimeProvider
    {
        private readonly Func<DateTimeOffset> _now;
        public FixedClock(Func<DateTimeOffset> now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now();
    }

    private readonly RetentionFactory _factory = new();
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public AuditRetentionTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        _client.Dispose();
    }

    /// <summary>
    /// Inserts an audit event with a custom <c>Timestamp</c> by
    /// temporarily winding the shared <see cref="TimeProvider"/>
    /// to the target moment, appending normally so the chain
    /// hashes the canonical body with that timestamp, then
    /// restoring the test's "now" override. Keeps chain integrity
    /// intact (a post-hoc UPDATE would break verification).
    /// </summary>
    private async Task SeedAtAsync(DateTimeOffset timestamp, string entityId)
    {
        var savedNow = _factory.NowOverride;
        _factory.NowOverride = timestamp;
        try
        {
            using var scope = _factory.Services.CreateScope();
            var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();
            await chain.AppendAsync(new AuditAppendRequest(
                Action: "policy.update",
                EntityType: "Policy",
                EntityId: entityId,
                FieldDiffJson: "[]",
                Rationale: $"seed {entityId}",
                ActorSubjectId: "user:test",
                ActorRoles: new[] { "admin" }), CancellationToken.None);
        }
        finally
        {
            _factory.NowOverride = savedNow;
        }
    }

    [Fact]
    public async Task List_RetentionDisabled_ReturnsAllEvents()
    {
        // ADR 0006.1 acceptance criterion (a): retentionDays = 0 →
        // default `from` is unset, full history visible.
        _factory.Retention.Threshold = null;
        await SeedAtAsync(_factory.NowOverride.AddDays(-365), "old");
        await SeedAtAsync(_factory.NowOverride.AddDays(-1), "recent");

        var resp = await _client.GetAsync("/api/audit");

        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<AuditPageDto>(JsonOpts);
        page!.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task List_RetentionPositive_DefaultsFromToThreshold()
    {
        // ADR 0006.1 acceptance criterion (b): retentionDays = 30 →
        // default `from` is now() - 30d. Events older than that are
        // hidden from the default list result.
        _factory.Retention.Threshold = _factory.NowOverride - TimeSpan.FromDays(30);
        await SeedAtAsync(_factory.NowOverride.AddDays(-90), "old");
        await SeedAtAsync(_factory.NowOverride.AddDays(-10), "fresh");

        var resp = await _client.GetAsync("/api/audit");

        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<AuditPageDto>(JsonOpts);
        page!.Items.Should().ContainSingle()
            .Which.EntityId.Should().Be("fresh");
    }

    [Fact]
    public async Task List_ExplicitFrom_OverridesRetentionDefault()
    {
        // ADR 0006.1 acceptance criterion (c): explicit ?from=
        // always wins over the threshold, even when the explicit
        // value predates it. Operators dig into older rows on
        // demand for incident response.
        _factory.Retention.Threshold = _factory.NowOverride - TimeSpan.FromDays(30);
        await SeedAtAsync(_factory.NowOverride.AddDays(-90), "old");
        await SeedAtAsync(_factory.NowOverride.AddDays(-10), "fresh");

        var explicitFrom = (_factory.NowOverride - TimeSpan.FromDays(180)).ToString("o");
        var resp = await _client.GetAsync($"/api/audit?from={Uri.EscapeDataString(explicitFrom)}");

        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<AuditPageDto>(JsonOpts);
        page!.Items.Should().HaveCount(2,
            "explicit ?from earlier than the threshold must surface older rows");
    }

    [Fact]
    public async Task Verify_IgnoresRetention_ReadsFullChain()
    {
        // ADR 0006.1 acceptance criterion (d): verify scope is the
        // integrity contract — the setting MUST NOT narrow it.
        // Threshold is set, but verify still inspects every row.
        _factory.Retention.Threshold = _factory.NowOverride - TimeSpan.FromDays(1);
        await SeedAtAsync(_factory.NowOverride.AddDays(-90), "old1");
        await SeedAtAsync(_factory.NowOverride.AddDays(-90), "old2");
        await SeedAtAsync(_factory.NowOverride.AddDays(-1), "fresh");

        var resp = await _client.GetAsync("/api/audit/verify");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("valid").GetBoolean().Should().BeTrue();
        body.GetProperty("inspectedCount").GetInt64().Should().Be(3,
            "verify must inspect every row regardless of staleness");
    }

    [Fact]
    public async Task Export_StaleEvents_GetStaleFlag()
    {
        // ADR 0006.1 acceptance criterion (e): NDJSON export
        // annotates events older than threshold with "stale": true;
        // fresh events have no stale field at all.
        _factory.Retention.Threshold = _factory.NowOverride - TimeSpan.FromDays(30);
        await SeedAtAsync(_factory.NowOverride.AddDays(-90), "old");
        await SeedAtAsync(_factory.NowOverride.AddDays(-10), "fresh");

        using var scope = _factory.Services.CreateScope();
        var exporter = scope.ServiceProvider.GetRequiredService<IAuditExporter>();
        await using var buffer = new MemoryStream();
        await exporter.WriteNdjsonAsync(buffer, fromSeq: null, toSeq: null, CancellationToken.None);

        var lines = Encoding.UTF8.GetString(buffer.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var eventLines = lines.Where(l => l.Contains("\"event\"")).ToList();
        eventLines.Should().HaveCount(2);

        var byEntity = eventLines
            .Select(l => JsonSerializer.Deserialize<JsonElement>(l))
            .ToDictionary(e => e.GetProperty("entityId").GetString()!);

        byEntity["old"].TryGetProperty("stale", out var oldStale).Should().BeTrue();
        oldStale.GetBoolean().Should().BeTrue();

        byEntity["fresh"].TryGetProperty("stale", out _).Should().BeFalse(
            "fresh events must NOT carry the stale field at all");
    }

    [Fact]
    public async Task Export_RetentionDisabled_NoStaleFlag()
    {
        // ADR 0006.1: with setting = 0, no event is ever flagged
        // stale even when older than any specific window.
        _factory.Retention.Threshold = null;
        await SeedAtAsync(_factory.NowOverride.AddDays(-365), "old");

        using var scope = _factory.Services.CreateScope();
        var exporter = scope.ServiceProvider.GetRequiredService<IAuditExporter>();
        await using var buffer = new MemoryStream();
        await exporter.WriteNdjsonAsync(buffer, fromSeq: null, toSeq: null, CancellationToken.None);

        var lines = Encoding.UTF8.GetString(buffer.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var eventLine = lines.Single(l => l.Contains("\"event\""));
        var parsed = JsonSerializer.Deserialize<JsonElement>(eventLine);
        parsed.TryGetProperty("stale", out _).Should().BeFalse();
    }
}
