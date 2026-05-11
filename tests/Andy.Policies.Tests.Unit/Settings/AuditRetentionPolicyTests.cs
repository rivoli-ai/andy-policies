// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Settings;
using Andy.Settings.Client;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Settings;

/// <summary>
/// Unit tests for <see cref="AuditRetentionPolicy"/> (ADR 0006.1,
/// story rivoli-ai/andy-policies#110). Pins the threshold-math
/// contract: 0 / negative / missing → no threshold; positive → the
/// expected past timestamp.
/// </summary>
public class AuditRetentionPolicyTests
{
    private sealed class StubSnapshot : ISettingsSnapshot
    {
        public int? StoredInt { get; set; }
        public bool? GetBool(string key) => null;
        public string? GetString(string key) => null;
        public int? GetInt(string key) =>
            key == AuditRetentionPolicy.SettingKey ? StoredInt : null;
        public IReadOnlyCollection<string> Keys => Array.Empty<string>();
        public DateTimeOffset? LastRefreshedAt => null;
    }

    private static readonly DateTimeOffset Now =
        new(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetStalenessThreshold_SettingZero_ReturnsNull()
    {
        var policy = new AuditRetentionPolicy(new StubSnapshot { StoredInt = 0 });
        policy.GetStalenessThreshold(Now).Should().BeNull(
            "0 means 'retain forever' — no event is ever stale, no default cut-off");
    }

    [Fact]
    public void GetStalenessThreshold_SettingMissing_ReturnsNull()
    {
        var policy = new AuditRetentionPolicy(new StubSnapshot { StoredInt = null });
        policy.GetStalenessThreshold(Now).Should().BeNull(
            "a snapshot that has not observed the key is treated as the manifest default (0)");
    }

    [Fact]
    public void GetStalenessThreshold_SettingNegative_TreatedAsZero()
    {
        var policy = new AuditRetentionPolicy(new StubSnapshot { StoredInt = -7 });
        policy.GetStalenessThreshold(Now).Should().BeNull(
            "a negative reading is clamped to 0 — operator error must not silently filter events");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(365)]
    public void GetStalenessThreshold_SettingPositive_ReturnsNowMinusDays(int days)
    {
        var policy = new AuditRetentionPolicy(new StubSnapshot { StoredInt = days });
        policy.GetStalenessThreshold(Now).Should().Be(
            Now - TimeSpan.FromDays(days));
    }

    [Fact]
    public void SettingKey_MatchesRegistrationManifestKey()
    {
        AuditRetentionPolicy.SettingKey.Should().Be("andy.policies.auditRetentionDays");
    }
}
