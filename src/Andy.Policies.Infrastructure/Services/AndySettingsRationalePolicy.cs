// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.Metrics;
using Andy.Policies.Application.Interfaces;
using Andy.Settings.Client;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Live <see cref="IRationalePolicy"/> backed by andy-settings (P2.4, #14).
/// Reads <see cref="SettingKey"/> from <see cref="ISettingsSnapshot"/> on
/// every check; the snapshot is refreshed by the package's hosted
/// <c>SettingsRefreshService</c> on a configurable cadence (default 60s,
/// per <c>AndySettings:Refresh</c>). A subsequent change to the toggle in
/// the andy-settings admin UI therefore takes effect on the next transition
/// after the next refresh, without an andy-policies restart.
/// <para>
/// Fail-safe: if the snapshot has not observed the key yet (cold start, or
/// andy-settings briefly unreachable), <see cref="IsRequired"/> defaults to
/// <c>true</c>. Operationally: a missing setting must never silently relax
/// audit requirements.
/// </para>
/// </summary>
public sealed class AndySettingsRationalePolicy : IRationalePolicy, IDisposable
{
    /// <summary>The andy-settings key, registered in
    /// <c>config/registration.json</c> with default <c>"true"</c>.</summary>
    public const string SettingKey = "andy.policies.rationaleRequired";

    /// <summary>OpenTelemetry meter name. Program.cs adds this meter to the
    /// metrics pipeline so the toggle value is exported to OTLP. Tests use
    /// the same name with <c>MeterListener</c> to assert the gauge value.</summary>
    public const string MeterName = "Andy.Policies";

    private readonly ISettingsSnapshot _snapshot;
    private readonly Meter _meter;

    public AndySettingsRationalePolicy(ISettingsSnapshot snapshot)
    {
        _snapshot = snapshot;
        _meter = new Meter(MeterName);

        // Observable gauge reports 1 / 0 on every collection so operators can
        // confirm the toggle reached the live process. The callback closes over
        // _snapshot, so a runtime flip in andy-settings (visible via the next
        // SettingsRefreshService tick) shows up on the next metrics scrape with
        // no extra plumbing.
        _meter.CreateObservableGauge(
            name: "andy_policies_rationale_required_toggle_value",
            observeValue: () => IsRequired ? 1 : 0,
            description: "Current value of andy.policies.rationaleRequired (1 = on, 0 = off).");
    }

    public bool IsRequired => _snapshot.GetBool(SettingKey) ?? true;

    public string? ValidateRationale(string? rationale)
    {
        if (!IsRequired)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(rationale))
        {
            return "Rationale is required and may not be empty or whitespace.";
        }
        return null;
    }

    public void Dispose() => _meter.Dispose();
}
