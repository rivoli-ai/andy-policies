// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.Metrics;
using Andy.Policies.Application.Interfaces;
using Andy.Settings.Client;

namespace Andy.Policies.Infrastructure.Settings;

/// <summary>
/// Live <see cref="IAuditRetentionPolicy"/> backed by andy-settings
/// (ADR 0006.1, story rivoli-ai/andy-policies#110). Mirrors the
/// shape of <see cref="PinningPolicy"/> + <see cref="ExperimentalOverridesGate"/>:
/// read fresh from <see cref="ISettingsSnapshot"/> on every call,
/// with an OTel gauge so operators can confirm the toggle reached the
/// live process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default = 0 (forever).</b> The shipped default in
/// <c>config/registration.json</c> is <c>0</c>. A negative value is
/// treated as <c>0</c> (no threshold); a missing observation is also
/// <c>0</c>. The setting cannot truncate the chain, so a permissive
/// default under a transient settings outage carries no integrity risk.
/// </para>
/// </remarks>
public sealed class AuditRetentionPolicy : IAuditRetentionPolicy, IDisposable
{
    public const string SettingKey = "andy.policies.auditRetentionDays";
    public const string MeterName = "Andy.Policies.AuditRetentionPolicy";

    private readonly ISettingsSnapshot _snapshot;
    private readonly Meter _meter;

    public AuditRetentionPolicy(ISettingsSnapshot snapshot)
    {
        _snapshot = snapshot;
        _meter = new Meter(MeterName);
        _meter.CreateObservableGauge(
            name: "andy_policies_audit_retention_days",
            observeValue: () => RetentionDays,
            description: "Current value of andy.policies.auditRetentionDays (0 = forever).");
    }

    private int RetentionDays
    {
        get
        {
            var raw = _snapshot.GetInt(SettingKey) ?? 0;
            return raw < 0 ? 0 : raw;
        }
    }

    public DateTimeOffset? GetStalenessThreshold(DateTimeOffset now)
    {
        var days = RetentionDays;
        return days == 0 ? null : now - TimeSpan.FromDays(days);
    }

    public void Dispose() => _meter.Dispose();
}
