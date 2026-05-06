// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Tests.E2E.EmbeddedSmoke;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.E2E;

/// <summary>
/// P10.4 (#39): pin <see cref="EmbeddedTestEnvironment"/>'s env-var
/// reads. The whole point of this helper is to keep URLs out of
/// source — the unit tests prove fallbacks and overrides land where
/// expected so a Conductor harness override can't silently miss.
/// </summary>
public class EmbeddedTestEnvironmentTests
{
    [Fact]
    public void Defaults_WhenAllVarsUnset_TrackComposeFile()
    {
        var env = MakeEnv(_ => null);

        env.IsEnabled.Should().BeFalse();
        env.SkipCompose.Should().BeFalse();
        env.PoliciesBaseUrl.AbsoluteUri.Should().Be("http://localhost:7113/");
        env.AuthBaseUrl.AbsoluteUri.Should().Be("http://localhost:7002/");
        env.ApiClientId.Should().Be("andy-policies-api");
        env.Audience.Should().Be("urn:andy-policies-api");
        env.ComposeFile.Should().Be("docker-compose.e2e.yml");
        env.ComposeWaitSeconds.Should().Be(90);
    }

    [Fact]
    public void E2EEnabled_OnlyWhenLiteralOne()
    {
        MakeEnv(k => k == EmbeddedTestEnvironment.EnabledFlag ? "1" : null)
            .IsEnabled.Should().BeTrue();
        MakeEnv(k => k == EmbeddedTestEnvironment.EnabledFlag ? "true" : null)
            .IsEnabled.Should().BeFalse("only the literal '1' enables — keeps the gate explicit");
        MakeEnv(k => k == EmbeddedTestEnvironment.EnabledFlag ? "0" : null)
            .IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void NoCompose_OnlyWhenLiteralOne()
    {
        MakeEnv(k => k == EmbeddedTestEnvironment.NoComposeFlag ? "1" : null)
            .SkipCompose.Should().BeTrue();
        MakeEnv(k => k == EmbeddedTestEnvironment.NoComposeFlag ? "true" : null)
            .SkipCompose.Should().BeFalse();
    }

    [Fact]
    public void ConductorHarness_OverridesPointAtProxy()
    {
        // Mirrors how Conductor Epic AO will wire the smoke against
        // the embedded :9100 proxy — every URL configurable, no code
        // change needed on the andy-policies side.
        var overrides = new Dictionary<string, string?>
        {
            [EmbeddedTestEnvironment.PoliciesBaseUrlVar] = "http://localhost:9100/policies",
            [EmbeddedTestEnvironment.AuthBaseUrlVar] = "http://localhost:9100/auth",
            [EmbeddedTestEnvironment.NoComposeFlag] = "1",
            [EmbeddedTestEnvironment.EnabledFlag] = "1",
        };
        var env = MakeEnv(k => overrides.GetValueOrDefault(k));

        env.IsEnabled.Should().BeTrue();
        env.SkipCompose.Should().BeTrue();
        env.PoliciesBaseUrl.AbsoluteUri.Should().Be("http://localhost:9100/policies/");
        env.AuthBaseUrl.AbsoluteUri.Should().Be("http://localhost:9100/auth/");
    }

    [Fact]
    public void BaseUrls_WithoutTrailingSlash_AreNormalised()
    {
        // Uri composition with relative paths breaks if the base URL
        // doesn't end in '/'. The helper must coerce.
        var env = MakeEnv(k => k == EmbeddedTestEnvironment.PoliciesBaseUrlVar
            ? "http://example.invalid/policies"
            : null);

        env.PoliciesBaseUrl.AbsoluteUri.Should().Be("http://example.invalid/policies/");
        new Uri(env.PoliciesBaseUrl, "health").AbsoluteUri
            .Should().Be("http://example.invalid/policies/health");
    }

    [Fact]
    public void ComposeWaitSeconds_NonIntegerValue_FallsBackToDefault()
    {
        var env = MakeEnv(k => k == EmbeddedTestEnvironment.ComposeWaitSecondsVar ? "abc" : null);

        env.ComposeWaitSeconds.Should().Be(90);
    }

    [Fact]
    public void ComposeWaitSeconds_NegativeValue_FallsBackToDefault()
    {
        var env = MakeEnv(k => k == EmbeddedTestEnvironment.ComposeWaitSecondsVar ? "-1" : null);

        env.ComposeWaitSeconds.Should().Be(90);
    }

    private static EmbeddedTestEnvironment MakeEnv(Func<string, string?> reader)
    {
        // Internal test seam — Tests.E2E InternalsVisibleTo doesn't
        // currently allow Tests.Unit, so we use reflection through
        // the public-by-arity ctor only when the seam is internal.
        // (As of P10.4 the seam IS internal, but Tests.E2E grants
        // access to Tests.Unit via an InternalsVisibleTo we add now.)
        return new EmbeddedTestEnvironment(reader);
    }
}
