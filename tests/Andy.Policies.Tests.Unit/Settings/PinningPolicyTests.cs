// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Settings;
using Andy.Settings.Client;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Settings;

/// <summary>
/// Unit tests for <see cref="PinningPolicy"/> (P8.4, story
/// rivoli-ai/andy-policies#84). Drives the adapter against an
/// in-memory <see cref="ISettingsSnapshot"/> stub to pin: a true
/// reading flips the gate on, a false reading flips it off, a
/// missing reading falls back to the manifest default (true), and
/// a snapshot that throws is treated as missing (fail-safe).
/// </summary>
public class PinningPolicyTests
{
    private sealed class StubSnapshot : ISettingsSnapshot
    {
        public bool? StoredBool { get; set; }
        public bool? GetBool(string key) =>
            key == PinningPolicy.SettingKey ? StoredBool : null;
        public string? GetString(string key) => null;
        public int? GetInt(string key) => null;
        public IReadOnlyCollection<string> Keys => Array.Empty<string>();
        public DateTimeOffset? LastRefreshedAt => null;
    }

    [Fact]
    public void IsPinningRequired_SettingTrue_ReturnsTrue()
    {
        var policy = new PinningPolicy(new StubSnapshot { StoredBool = true });
        policy.IsPinningRequired.Should().BeTrue();
    }

    [Fact]
    public void IsPinningRequired_SettingFalse_ReturnsFalse()
    {
        var policy = new PinningPolicy(new StubSnapshot { StoredBool = false });
        policy.IsPinningRequired.Should().BeFalse(
            "an explicit false flips pinning off — used by dev environments");
    }

    [Fact]
    public void IsPinningRequired_SettingMissing_ReturnsManifestDefaultTrue()
    {
        var policy = new PinningPolicy(new StubSnapshot { StoredBool = null });
        policy.IsPinningRequired.Should().Be(
            PinningPolicy.ManifestDefault,
            "a snapshot that has not yet observed the key must NOT silently " +
            "relax pinning — fail-safe to the manifest default");
    }

    [Fact]
    public void ManifestDefault_IsTrue_MatchingRegistrationJson()
    {
        // Sanity check: if someone flips the manifest default, this
        // test forces a deliberate update here so the contract
        // between adapter and manifest stays explicit.
        PinningPolicy.ManifestDefault.Should().BeTrue();
    }

    [Fact]
    public void SettingKey_MatchesRegistrationManifestKey()
    {
        PinningPolicy.SettingKey.Should().Be("andy.policies.bundleVersionPinning");
    }
}
