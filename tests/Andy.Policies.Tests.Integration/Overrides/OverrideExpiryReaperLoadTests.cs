// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.BackgroundServices;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andy.Policies.Tests.Integration.Overrides;

/// <summary>
/// P5.8 (#62) — load test for the override-expiry reaper. Seeds a
/// large mixed-state dataset (5,000 due, 5,000 future) and drives
/// successive sweeps until the due rows are drained. Verifies:
///   - All due rows transition to <see cref="OverrideState.Expired"/>;
///     no future rows are touched.
///   - The drain finishes within a wall-clock budget (memory-bound
///     pages on the standard CI runner are the constraint;
///     <c>MaxRowsPerSweep = 500</c> means ≥10 sweeps for 5,000 rows).
///   - Heap delta stays below a sane bound — the reaper is a
///     batch-style worker and must not retain references across
///     sweeps.
///
/// Gated behind the <c>Perf</c> trait so the standard
/// <c>dotnet test</c> run on developer machines skips it; CI's
/// dedicated perf job opts in via <c>--filter Category=Perf</c>.
/// </summary>
[Trait("Category", "Perf")]
public class OverrideExpiryReaperLoadTests : IDisposable
{
    private const int DueRows = 5_000;
    private const int FutureRows = 5_000;

    // 90s wall budget + 50-sweep cap give significant headroom over
    // the dev-laptop measurement (~10 sweeps in ~5s). CI runners
    // (ubuntu-latest shared) are notoriously variable; the budget
    // is sized to fail on a genuinely broken implementation, not on
    // a slow runner. 5,000 rows / 500 cap = 10 sweeps minimum.
    private const int WallClockBudgetSeconds = 90;
    private const int MaxSweepIterations = 50;
    private const long MaxHeapDeltaBytes = 200L * 1024 * 1024; // 200 MB

    private sealed class LoadFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

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
                _connection.Open();
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

                // Strip the reaper from IHostedService so the BackgroundService
                // executor is not running concurrently with the test's manual
                // SweepOnceAsync loop. Without this the two race on the same
                // due rows: the background sweep wins on some, the foreground
                // ExpireAsync then throws ConflictException and is swallowed,
                // so the test's locally-summed totalExpired falls below
                // DueRows even though the DB is fully drained. See #166.
                var hostedReaper = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(OverrideExpiryReaper));
                if (hostedReaper is not null) services.Remove(hostedReaper);
                services.TryAddSingleton<OverrideExpiryReaper>();

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

    private readonly LoadFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static OverrideExpiryReaper ResolveReaper(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<OverrideExpiryReaper>();

    [Fact]
    public async Task SweepUntilDrained_ExpiresAllDueRows_LeavesFutureUntouched_WithinBudget()
    {
        // Trigger host startup so DI is built. The reaper is registered as
        // a singleton (not as IHostedService) for this factory — see the
        // configure-services block above for why.
        _ = _factory.CreateClient();
        var rootSp = _factory.Services;
        var reaper = ResolveReaper(_factory);

        // Seed: one Policy + one Active Version shared by every row,
        // then 5,000 due overrides and 5,000 future overrides. Done in
        // a single transaction to keep seed time short.
        using (var scope = rootSp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var policy = new Policy
            {
                Id = Guid.NewGuid(),
                Name = $"reaper-load-{Guid.NewGuid():n}",
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
            db.Policies.Add(policy);
            db.PolicyVersions.Add(version);

            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < DueRows; i++)
            {
                db.Overrides.Add(NewApprovedOverride(version.Id, now.AddSeconds(-1 - i), kind: "due"));
            }
            for (var i = 0; i < FutureRows; i++)
            {
                db.Overrides.Add(NewApprovedOverride(version.Id, now.AddDays(1).AddSeconds(i), kind: "future"));
            }
            await db.SaveChangesAsync();
        }

        // Drain the due set with successive sweeps. Cap of 500 rows
        // per sweep ⇒ at least 10 calls; allow up to 25 to absorb
        // batching overhead (DueRows / MaxRowsPerSweep + headroom).
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var stopwatch = Stopwatch.StartNew();
        var totalExpired = 0;
        for (var i = 0; i < MaxSweepIterations && totalExpired < DueRows; i++)
        {
            totalExpired += await reaper.SweepOnceAsync(CancellationToken.None);
            if (stopwatch.Elapsed.TotalSeconds > WallClockBudgetSeconds)
            {
                break; // budget exhausted; assertion below will report
            }
        }
        stopwatch.Stop();
        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

        totalExpired.Should().Be(DueRows);
        stopwatch.Elapsed.TotalSeconds.Should().BeLessThan(WallClockBudgetSeconds,
            $"draining {DueRows} due rows must finish within {WallClockBudgetSeconds}s");

        // Spot-check the future set is untouched. Use a fresh scope
        // to bypass any tracking.
        using (var scope = rootSp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var futureCount = await db.Overrides.AsNoTracking()
                .CountAsync(o => o.State == OverrideState.Approved
                                 && o.ScopeRef.StartsWith("user:future:"));
            futureCount.Should().Be(FutureRows);
        }

        var heapDelta = heapAfter - heapBefore;
        heapDelta.Should().BeLessThan(MaxHeapDeltaBytes,
            $"reaper must not retain references across sweeps (delta {heapDelta} > {MaxHeapDeltaBytes})");
    }

    private static Override NewApprovedOverride(Guid policyVersionId, DateTimeOffset expiresAt, string kind)
        => new()
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = policyVersionId,
            ScopeKind = OverrideScopeKind.Principal,
            ScopeRef = $"user:{kind}:{Guid.NewGuid():n}",
            Effect = OverrideEffect.Exempt,
            ProposerSubjectId = "user:proposer",
            ApproverSubjectId = "user:approver",
            State = OverrideState.Approved,
            ProposedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            ApprovedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            ExpiresAt = expiresAt,
            Rationale = "load-test fixture",
        };
}
