// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.BackgroundServices;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Tests.Integration.Controllers;
using Andy.Settings.Client;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andy.Policies.Tests.Integration.BackgroundServices;

/// <summary>
/// P5.3 (#53) — exercises <see cref="OverrideExpiryReaper"/> against a
/// real <c>WebApplicationFactory&lt;Program&gt;</c> + SQLite host.
/// We drive <c>SweepOnceAsync</c> directly rather than waiting for the
/// periodic timer: the periodic timer is verified structurally by
/// <see cref="Reaper_HostedServiceIsRegistered"/>, while the integration
/// payload (real DbContext, real OverrideService, real serializable
/// transaction over SQLite) is what these tests need to exercise.
/// </summary>
public class OverrideExpiryReaperIntegrationTests
{
    private sealed class ReaperSnapshot : ISettingsSnapshot
    {
        public int? CadenceSeconds { get; set; }

        public bool? ExperimentalOverridesEnabled { get; set; }

        public int? GetInt(string key) =>
            key == OverrideExpiryReaper.CadenceSettingKey ? CadenceSeconds : null;

        public bool? GetBool(string key) =>
            key == "andy.policies.experimentalOverridesEnabled"
                ? ExperimentalOverridesEnabled
                : null;

        public string? GetString(string key) => null;

        public IReadOnlyCollection<string> Keys => Array.Empty<string>();

        public DateTimeOffset? LastRefreshedAt => DateTimeOffset.UtcNow;
    }

    private sealed class ReaperFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public ReaperSnapshot Snapshot { get; } = new() { CadenceSeconds = 60 };

        public ReaperFactory()
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

                var snapshotDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(ISettingsSnapshot));
                if (snapshotDescriptor is not null) services.Remove(snapshotDescriptor);
                services.AddSingleton<ISettingsSnapshot>(Snapshot);

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

    private static OverrideExpiryReaper ResolveReaper(WebApplicationFactory<Program> factory)
    {
        // The reaper is registered as IHostedService; pull the live
        // instance out of the host's hosted-service collection so we
        // can drive SweepOnceAsync against the same DI container the
        // periodic loop would use.
        return factory.Services.GetServices<IHostedService>()
            .OfType<OverrideExpiryReaper>()
            .Single();
    }

    private static async Task<Override> SeedApprovedOverrideAsync(
        IServiceProvider rootSp, DateTimeOffset expiresAt)
    {
        using var scope = rootSp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = $"reaper-{Guid.NewGuid():n}",
            CreatedBySubjectId = "fixture",
        };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            Version = 1,
            State = LifecycleState.Active,
            Enforcement = EnforcementLevel.Should,
            Severity = Severity.Moderate,
            Scopes = new List<string>(),
            Summary = "fixture",
            RulesJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "fixture",
            ProposerSubjectId = "fixture",
        };
        var ovr = new Override
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = version.Id,
            ScopeKind = OverrideScopeKind.Principal,
            ScopeRef = $"user:reaper-{Guid.NewGuid():n}",
            Effect = OverrideEffect.Exempt,
            ProposerSubjectId = "user:proposer",
            ApproverSubjectId = "user:approver",
            State = OverrideState.Approved,
            ProposedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            ApprovedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            ExpiresAt = expiresAt,
            Rationale = "integration fixture",
        };

        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        db.Overrides.Add(ovr);
        await db.SaveChangesAsync();
        return ovr;
    }

    private static async Task<OverrideState> ReadStateAsync(IServiceProvider rootSp, Guid id)
    {
        using var scope = rootSp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Overrides.AsNoTracking().FirstAsync(o => o.Id == id);
        return row.State;
    }

    [Fact]
    public async Task SweepOnceAsync_ExpiresApprovedOverridePastDeadline_OverRealSqlite()
    {
        using var factory = new ReaperFactory();
        _ = factory.CreateClient();
        var rootSp = factory.Services;
        var reaper = ResolveReaper(factory);

        var ovr = await SeedApprovedOverrideAsync(rootSp,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));

        var count = await reaper.SweepOnceAsync(CancellationToken.None);

        count.Should().Be(1);
        var state = await ReadStateAsync(rootSp, ovr.Id);
        state.Should().Be(OverrideState.Expired);
    }

    [Fact]
    public async Task SweepOnceAsync_RunsWhenExperimentalOverridesGateIsOff()
    {
        // The settings gate from P5.4 only blocks new propose/approve;
        // it must not strand previously approved overrides past their
        // deadline. Verify the reaper sweeps regardless of the toggle.
        using var factory = new ReaperFactory
        {
            Snapshot = { ExperimentalOverridesEnabled = false },
        };
        _ = factory.CreateClient();
        var rootSp = factory.Services;
        var reaper = ResolveReaper(factory);

        var ovr = await SeedApprovedOverrideAsync(rootSp,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));

        var count = await reaper.SweepOnceAsync(CancellationToken.None);

        count.Should().Be(1);
        var state = await ReadStateAsync(rootSp, ovr.Id);
        state.Should().Be(OverrideState.Expired);
    }

    [Fact]
    public async Task SweepOnceAsync_LeavesFutureExpiriesAlone()
    {
        using var factory = new ReaperFactory();
        _ = factory.CreateClient();
        var rootSp = factory.Services;
        var reaper = ResolveReaper(factory);

        var ovr = await SeedApprovedOverrideAsync(rootSp,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        var count = await reaper.SweepOnceAsync(CancellationToken.None);

        count.Should().Be(0);
        var state = await ReadStateAsync(rootSp, ovr.Id);
        state.Should().Be(OverrideState.Approved);
    }

    [Fact]
    public async Task SweepOnceAsync_BatchOf25_ExpiresAllInOnePass()
    {
        // Exercises the multi-row path against real SQLite +
        // serializable transactions; each ExpireAsync runs its own
        // transaction inside the sweep loop.
        using var factory = new ReaperFactory();
        _ = factory.CreateClient();
        var rootSp = factory.Services;
        var reaper = ResolveReaper(factory);

        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            var o = await SeedApprovedOverrideAsync(rootSp,
                expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1 - i));
            ids.Add(o.Id);
        }

        var count = await reaper.SweepOnceAsync(CancellationToken.None);

        count.Should().Be(25);
        using var scope = rootSp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var states = await db.Overrides.AsNoTracking()
            .Where(o => ids.Contains(o.Id))
            .Select(o => o.State)
            .ToListAsync();
        states.Should().AllSatisfy(s => s.Should().Be(OverrideState.Expired));
    }

    [Fact]
    public void Reaper_HostedServiceIsRegistered()
    {
        // Sanity: P5.3 requires the reaper to be registered via
        // AddHostedService, not just available as a transient. Verifies
        // we don't accidentally lose the wiring in a future refactor.
        using var factory = new ReaperFactory();
        _ = factory.CreateClient();

        var hostedServices = factory.Services.GetServices<IHostedService>().ToList();
        hostedServices.Should().Contain(s => s is OverrideExpiryReaper);
    }
}
