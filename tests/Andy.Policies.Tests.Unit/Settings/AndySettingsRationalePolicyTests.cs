// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.Metrics;
using Andy.Policies.Infrastructure.Services;
using Andy.Settings.Client;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Settings;

/// <summary>
/// Unit coverage for <see cref="AndySettingsRationalePolicy"/> (P2.4, #14).
/// Drives the policy with a stub <see cref="ISettingsSnapshot"/> so each
/// toggle state — on, off, unknown — is tested without standing up the
/// andy-settings client. Also asserts the
/// <c>andy_policies_rationale_required_toggle_value</c> observable gauge
/// reports 1/0 in lockstep with <c>IsRequired</c>.
/// </summary>
public class AndySettingsRationalePolicyTests
{
    private sealed class StubSnapshot : ISettingsSnapshot
    {
        public bool? Value { get; set; }

        public bool? GetBool(string key) =>
            key == AndySettingsRationalePolicy.SettingKey ? Value : null;

        public string? GetString(string key) => null;

        public int? GetInt(string key) => null;

        public IReadOnlyCollection<string> Keys => Array.Empty<string>();

        public DateTimeOffset? LastRefreshedAt => null;
    }

    [Fact]
    public void IsRequired_True_RejectsNullEmptyAndWhitespace()
    {
        var policy = new AndySettingsRationalePolicy(new StubSnapshot { Value = true });

        policy.IsRequired.Should().BeTrue();
        policy.ValidateRationale(null).Should().NotBeNull();
        policy.ValidateRationale("").Should().NotBeNull();
        policy.ValidateRationale("  \t \n").Should().NotBeNull();
    }

    [Fact]
    public void IsRequired_True_AcceptsNonEmpty()
    {
        var policy = new AndySettingsRationalePolicy(new StubSnapshot { Value = true });

        policy.ValidateRationale("because canary passed").Should().BeNull();
    }

    [Fact]
    public void IsRequired_False_AcceptsAllInputsIncludingNull()
    {
        var policy = new AndySettingsRationalePolicy(new StubSnapshot { Value = false });

        policy.IsRequired.Should().BeFalse();
        policy.ValidateRationale(null).Should().BeNull();
        policy.ValidateRationale("").Should().BeNull();
        policy.ValidateRationale("anything").Should().BeNull();
    }

    [Fact]
    public void IsRequired_FailSafe_DefaultsTrue_WhenSnapshotHasNoValue()
    {
        // Cold start (snapshot not refreshed yet) or andy-settings unreachable
        // both surface as a null GetBool. Acceptance criterion: stricter audit
        // wins by default.
        var policy = new AndySettingsRationalePolicy(new StubSnapshot { Value = null });

        policy.IsRequired.Should().BeTrue();
        policy.ValidateRationale("").Should().NotBeNull();
    }

    [Fact]
    public void Snapshot_Flip_TakesEffectOnNextValidate()
    {
        var snapshot = new StubSnapshot { Value = true };
        var policy = new AndySettingsRationalePolicy(snapshot);

        policy.ValidateRationale("").Should().NotBeNull();

        snapshot.Value = false;
        policy.IsRequired.Should().BeFalse();
        policy.ValidateRationale("").Should().BeNull();
    }

    [Fact]
    public void ToggleGauge_ReportsCurrentValue_AsOneOrZero()
    {
        var snapshot = new StubSnapshot { Value = true };
        using var policy = new AndySettingsRationalePolicy(snapshot);

        var observed = new Dictionary<string, long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AndySettingsRationalePolicy.MeterName
                    && instrument.Name == "andy_policies_rationale_required_toggle_value")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<int>((inst, v, _, _) => observed[inst.Name] = v);
        listener.Start();

        listener.RecordObservableInstruments();
        observed["andy_policies_rationale_required_toggle_value"].Should().Be(1);

        snapshot.Value = false;
        listener.RecordObservableInstruments();
        observed["andy_policies_rationale_required_toggle_value"].Should().Be(0);
    }
}
