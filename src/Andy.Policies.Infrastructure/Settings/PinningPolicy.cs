// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.Metrics;
using Andy.Policies.Application.Interfaces;
using Andy.Settings.Client;

namespace Andy.Policies.Infrastructure.Settings;

/// <summary>
/// Live <see cref="IPinningPolicy"/> backed by andy-settings (P8.4,
/// story rivoli-ai/andy-policies#84). Mirrors
/// <see cref="ExperimentalOverridesGate"/> + the rationale-policy
/// shape: read fresh from <see cref="ISettingsSnapshot"/> on every
/// check, with an OTel gauge so operators can confirm the toggle
/// reached the live process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-safe default.</b> The shipped default in
/// <c>config/registration.json</c> is <c>true</c>; the snapshot
/// returns <c>null</c> until andy-settings has observed the key.
/// We coalesce to the manifest default — never relax pinning under
/// a transient settings outage.
/// </para>
/// <para>
/// <b>Hot reload.</b> The snapshot is refreshed by the package's
/// hosted refresh service on the configured cadence. A flip in the
/// andy-settings admin UI takes effect on the next read after the
/// next refresh, without an andy-policies restart.
/// </para>
/// </remarks>
public sealed class PinningPolicy : IPinningPolicy, IDisposable
{
    /// <summary>The andy-settings key, registered in
    /// <c>config/registration.json</c> with default <c>"true"</c>.</summary>
    public const string SettingKey = "andy.policies.bundleVersionPinning";

    /// <summary>Manifest default applied when the snapshot has no
    /// observation for the key. The catalog's reproducibility
    /// posture means missing → required.</summary>
    public const bool ManifestDefault = true;

    /// <summary>OpenTelemetry meter name. Matches the convention used
    /// by sibling settings adapters so a single pipeline registration
    /// surfaces every toggle gauge.</summary>
    public const string MeterName = "Andy.Policies.PinningPolicy";

    private readonly ISettingsSnapshot _snapshot;
    private readonly Meter _meter;

    public PinningPolicy(ISettingsSnapshot snapshot)
    {
        _snapshot = snapshot;
        _meter = new Meter(MeterName);
        _meter.CreateObservableGauge(
            name: "andy_policies_bundle_version_pinning_required",
            observeValue: () => IsPinningRequired ? 1 : 0,
            description: "Current value of andy.policies.bundleVersionPinning (1 = required, 0 = optional).");
    }

    public bool IsPinningRequired => _snapshot.GetBool(SettingKey) ?? ManifestDefault;

    public void Dispose() => _meter.Dispose();
}
