// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Settings;
using Andy.Settings.Client;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Settings;

/// <summary>
/// Unit coverage for <see cref="ExperimentalOverridesGate"/> (P5.4,
/// rivoli-ai/andy-policies#56). Drives the gate with a stub
/// <see cref="ISettingsSnapshot"/> so each toggle state — on, off,
/// unobserved — is tested without standing up the andy-settings
/// client. Asserts the fail-closed default (unobserved → false) is
/// what the production code path returns.
/// </summary>
public class ExperimentalOverridesGateTests
{
    private sealed class StubSnapshot : ISettingsSnapshot
    {
        public bool? Value { get; set; }

        public bool? GetBool(string key) =>
            key == ExperimentalOverridesGate.SettingKey ? Value : null;

        public string? GetString(string key) => null;

        public int? GetInt(string key) => null;

        public IReadOnlyCollection<string> Keys => Array.Empty<string>();

        public DateTimeOffset? LastRefreshedAt => null;
    }

    [Fact]
    public void IsEnabled_True_WhenSnapshotReportsTrue()
    {
        var snapshot = new StubSnapshot { Value = true };
        var gate = new ExperimentalOverridesGate(snapshot);

        gate.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_False_WhenSnapshotReportsFalse()
    {
        var snapshot = new StubSnapshot { Value = false };
        var gate = new ExperimentalOverridesGate(snapshot);

        gate.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_FailsClosed_WhenSnapshotHasNotObservedKey()
    {
        // Cold start, or andy-settings briefly unreachable: the
        // snapshot returns null for the key. The shipped default and
        // the safer state both want this path to read false.
        var snapshot = new StubSnapshot { Value = null };
        var gate = new ExperimentalOverridesGate(snapshot);

        gate.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_TracksLiveSnapshotChanges()
    {
        // Hot reload via andy-settings refresh: the snapshot value
        // flips while the gate instance is alive. The next read picks
        // up the new value without an andy-policies restart.
        var snapshot = new StubSnapshot { Value = false };
        var gate = new ExperimentalOverridesGate(snapshot);
        gate.IsEnabled.Should().BeFalse();

        snapshot.Value = true;
        gate.IsEnabled.Should().BeTrue();

        snapshot.Value = false;
        gate.IsEnabled.Should().BeFalse();
    }
}
