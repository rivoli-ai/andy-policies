// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Andy.Policies.Tests.E2E.EmbeddedSmoke;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.E2E;

/// <summary>
/// P10.4 (#39): proves the no-compose flag is wired so the Conductor
/// Epic AO harness — which manages its own compose stack — never
/// accidentally races the fixture for control.
/// </summary>
public class DockerComposeHelperTests
{
    [Fact]
    public async Task NoComposeFlag_Up_IsNoop()
    {
        var env = new EmbeddedTestEnvironment(
            k => k == EmbeddedTestEnvironment.NoComposeFlag ? "1" : null);
        var startCount = 0;
        var helper = new DockerComposeHelper(env, workingDirectory: "/tmp",
            startProcess: _ => { startCount++; return null; });

        await helper.UpAsync(CancellationToken.None);

        startCount.Should().Be(0, "no docker invocation when NO_COMPOSE=1");
        helper.DidStartCompose.Should().BeFalse();
    }

    [Fact]
    public async Task NoComposeFlag_Down_IsNoop()
    {
        var env = new EmbeddedTestEnvironment(
            k => k == EmbeddedTestEnvironment.NoComposeFlag ? "1" : null);
        var startCount = 0;
        var helper = new DockerComposeHelper(env, workingDirectory: "/tmp",
            startProcess: _ => { startCount++; return null; });

        await helper.DownAsync(CancellationToken.None);

        startCount.Should().Be(0);
    }

    [Fact]
    public async Task DownAsync_WhenUpNeverRan_DoesNotInvokeDocker()
    {
        // Defensive: an exception during InitializeAsync between
        // skip-compose check and `up` would otherwise leave Down
        // trying to take down a stack we never started.
        var env = new EmbeddedTestEnvironment(_ => null);
        var startCount = 0;
        var helper = new DockerComposeHelper(env, workingDirectory: "/tmp",
            startProcess: _ => { startCount++; return null; });

        await helper.DownAsync(CancellationToken.None);

        startCount.Should().Be(0);
        helper.DidStartCompose.Should().BeFalse();
    }
}
