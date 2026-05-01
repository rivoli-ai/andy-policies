// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.Metrics;
using Andy.Policies.Application.Settings;
using Andy.Settings.Client;

namespace Andy.Policies.Infrastructure.Settings;

/// <summary>
/// Live <see cref="IExperimentalOverridesGate"/> backed by andy-settings
/// (P5.4, rivoli-ai/andy-policies#56). Mirrors the
/// <c>AndySettingsRationalePolicy</c> shape: read fresh from
/// <see cref="ISettingsSnapshot"/> on every check, with an OTel gauge
/// so operators can confirm the toggle reached the live process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-closed default:</b> if the snapshot has not yet observed the
/// key (cold start, or andy-settings briefly unreachable),
/// <see cref="IsEnabled"/> returns <c>false</c>. The setting is a
/// feature flag; <c>false</c> is both the shipped default and the
/// safer state when in doubt — turning experimental writes off can't
/// break correctness, while the wrong default of <c>true</c> would.
/// </para>
/// <para>
/// <b>Hot reload:</b> the snapshot is refreshed by andy-settings'
/// hosted <c>SettingsRefreshService</c> on the configured cadence
/// (default 60s). A flip in the andy-settings admin UI takes effect
/// on the next override write after the next refresh, without an
/// andy-policies restart.
/// </para>
/// </remarks>
public sealed class ExperimentalOverridesGate : IExperimentalOverridesGate, IDisposable
{
    /// <summary>The andy-settings key, registered in
    /// <c>config/registration.json</c> with default <c>false</c>.</summary>
    public const string SettingKey = "andy.policies.experimentalOverridesEnabled";

    /// <summary>OpenTelemetry meter name. <c>Program.cs</c> adds this
    /// meter to the metrics pipeline so the toggle value is exported
    /// to OTLP for operator visibility.</summary>
    public const string MeterName = "Andy.Policies.ExperimentalOverridesGate";

    private readonly ISettingsSnapshot _snapshot;
    private readonly Meter _meter;

    public ExperimentalOverridesGate(ISettingsSnapshot snapshot)
    {
        _snapshot = snapshot;
        _meter = new Meter(MeterName);
        _meter.CreateObservableGauge(
            name: "andy_policies_experimental_overrides_enabled",
            observeValue: () => IsEnabled ? 1 : 0,
            description: "Current value of andy.policies.experimentalOverridesEnabled (1 = on, 0 = off).");
    }

    public bool IsEnabled => _snapshot.GetBool(SettingKey) ?? false;

    public void Dispose() => _meter.Dispose();
}
