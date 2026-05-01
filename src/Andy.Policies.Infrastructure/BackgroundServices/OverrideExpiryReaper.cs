// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Settings.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.BackgroundServices;

/// <summary>
/// Sweeps approved overrides past <c>ExpiresAt</c> into
/// <see cref="OverrideState.Expired"/> on a configurable cadence
/// (P5.3, story rivoli-ai/andy-policies#53). The reaper is the only
/// path into <c>Expired</c> — operator-initiated revocation goes to
/// <c>Revoked</c> via <see cref="IOverrideService.RevokeAsync"/> — so
/// audit (P6) can distinguish system expiry from user revocation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hosted service rather than pg_cron / a per-row timer?</b>
/// pg_cron is Postgres-only (we'd break embedded SQLite mode), and
/// per-override timers don't survive process restart. A periodic
/// in-process sweep is also the only design that emits domain events
/// — so audit can record expiry as <c>actor=system:reaper</c>.
/// </para>
/// <para>
/// <b>Cadence</b> is read fresh from <see cref="ISettingsSnapshot"/>
/// every tick (key <see cref="CadenceSettingKey"/>, default
/// <see cref="DefaultCadenceSeconds"/>). A change in andy-settings
/// admin UI takes effect on the *next* sweep without a restart.
/// Cadence is clamped to <see cref="MinCadenceSeconds"/> to prevent
/// hot-looping under operator misconfiguration.
/// </para>
/// <para>
/// <b>Settings gate independence:</b> the reaper runs even when
/// <c>andy.policies.experimentalOverridesEnabled = false</c>.
/// Otherwise, turning the feature off would strand previously-approved
/// overrides past their expiry — a security footgun. The gate (P5.4)
/// only blocks new proposals and approvals.
/// </para>
/// <para>
/// <b>Failure mode:</b> exceptions during a sweep are logged and
/// swallowed; the loop continues on the next tick. Per-row failures
/// (e.g. a race where another actor revoked the override between scan
/// and expire) are caught via <see cref="ConflictException"/> and
/// also swallowed — the reaper is idempotent by design.
/// </para>
/// </remarks>
public sealed class OverrideExpiryReaper : BackgroundService
{
    /// <summary>The andy-settings key, registered in
    /// <c>config/registration.json</c> with default
    /// <see cref="DefaultCadenceSeconds"/>.</summary>
    public const string CadenceSettingKey = "andy.policies.overrideExpiryReaperCadenceSeconds";

    /// <summary>OpenTelemetry meter name. <c>Program.cs</c> adds this
    /// meter to the metrics pipeline so reaper telemetry is exported
    /// to OTLP.</summary>
    public const string MeterName = "Andy.Policies.OverrideExpiryReaper";

    public const int DefaultCadenceSeconds = 60;

    /// <summary>Lower bound on the sweep cadence. Prevents an
    /// operator misconfiguration (e.g. <c>0</c>) from turning the
    /// reaper into a hot loop.</summary>
    public const int MinCadenceSeconds = 5;

    /// <summary>Per-sweep cap. Keeps individual transactions short
    /// even under a backlog (e.g. an outage that prevented sweeping
    /// for hours). Subsequent sweeps drain the rest.</summary>
    public const int MaxRowsPerSweep = 500;

    private readonly IServiceScopeFactory _scopes;
    private readonly ISettingsSnapshot _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<OverrideExpiryReaper> _log;

    private readonly Meter _meter;
    private readonly Counter<long> _sweptCounter;
    private readonly Counter<long> _failuresCounter;
    private readonly Histogram<double> _sweepDuration;

    public OverrideExpiryReaper(
        IServiceScopeFactory scopes,
        ISettingsSnapshot settings,
        TimeProvider clock,
        ILogger<OverrideExpiryReaper> log)
    {
        _scopes = scopes;
        _settings = settings;
        _clock = clock;
        _log = log;

        _meter = new Meter(MeterName);
        _sweptCounter = _meter.CreateCounter<long>(
            "policies.override.reaper.swept",
            description: "Number of overrides expired by a sweep.");
        _failuresCounter = _meter.CreateCounter<long>(
            "policies.override.reaper.failures",
            description: "Number of per-sweep or per-row failures encountered.");
        _sweepDuration = _meter.CreateHistogram<double>(
            "policies.override.reaper.sweep_duration",
            unit: "s",
            description: "Wall-clock duration of a single sweep.");
    }

    public int CurrentCadenceSeconds
    {
        get
        {
            var raw = _settings.GetInt(CadenceSettingKey) ?? DefaultCadenceSeconds;
            return Math.Max(MinCadenceSeconds, raw);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "OverrideExpiryReaper started (cadence {Cadence}s, min {Min}s, cap {Cap} rows/sweep)",
            CurrentCadenceSeconds, MinCadenceSeconds, MaxRowsPerSweep);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _failuresCounter.Add(1, new KeyValuePair<string, object?>("phase", "sweep"));
                _log.LogError(ex, "OverrideExpiryReaper sweep failed; will retry next tick");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(CurrentCadenceSeconds),
                    _clock,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.LogInformation("OverrideExpiryReaper stopping");
    }

    /// <summary>
    /// Single sweep pass. Public so unit tests can drive it without
    /// spinning up the whole hosted-service loop; production code
    /// should rely on the periodic <see cref="ExecuteAsync"/>
    /// schedule rather than calling this directly.
    /// </summary>
    /// <returns>The count of overrides successfully expired.</returns>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IOverrideService>();
        var now = _clock.GetUtcNow();

        // Filter on State server-side (covered by ix_overrides_scope_state
        // and ix_overrides_expiry_approved), then refine on ExpiresAt
        // client-side. SQLite cannot translate DateTimeOffset
        // comparisons or ordering — same posture as the rest of the
        // codebase (see PolicyService list filters). The Approved set
        // is bounded in practice; the cap below still applies.
        var approved = await db.Overrides
            .AsNoTracking()
            .Where(o => o.State == OverrideState.Approved)
            .Select(o => new { o.Id, o.ExpiresAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var dueIds = approved
            .Where(o => o.ExpiresAt <= now)
            .OrderBy(o => o.ExpiresAt)
            .Take(MaxRowsPerSweep)
            .Select(o => o.Id)
            .ToList();

        var expired = 0;
        foreach (var id in dueIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await service.ExpireAsync(id, ct).ConfigureAwait(false);
                expired++;
            }
            catch (NotFoundException)
            {
                // Race: row was deleted (or never visible to this scope's
                // tracker). Safe to skip — the next sweep will retry.
                _failuresCounter.Add(1, new KeyValuePair<string, object?>("phase", "row"));
            }
            catch (ConflictException)
            {
                // Race: another actor revoked the override between scan
                // and expire, or someone bumped ExpiresAt forward.
                // The reaper is idempotent — continue with the next id.
                _failuresCounter.Add(1, new KeyValuePair<string, object?>("phase", "row"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-row exceptions must not abort the sweep — a single
                // poison row would otherwise stall every subsequent
                // expiry. Log + count + continue.
                _failuresCounter.Add(1, new KeyValuePair<string, object?>("phase", "row"));
                _log.LogWarning(ex,
                    "OverrideExpiryReaper failed to expire {OverrideId}; continuing", id);
            }
        }

        stopwatch.Stop();
        _sweptCounter.Add(expired);
        _sweepDuration.Record(stopwatch.Elapsed.TotalSeconds);

        if (expired > 0)
        {
            _log.LogInformation(
                "OverrideExpiryReaper expired {Count} overrides in {Elapsed:n0}ms",
                expired, stopwatch.Elapsed.TotalMilliseconds);
        }

        return expired;
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}
