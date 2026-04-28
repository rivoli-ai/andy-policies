// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using Andy.Policies.Cli.Commands;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Cli;

/// <summary>
/// Command-tree shape tests for P2.7 (#17). Asserts that the
/// <c>versions</c> noun gets <c>publish</c>, <c>wind-down</c>, and
/// <c>retire</c> subcommands, each with the expected positional arguments
/// and a <c>--rationale</c> / <c>-r</c> option. Catches accidental rename or
/// option-removal during refactors before integration tests run.
/// </summary>
public class VersionsLifecycleCommandsTests
{
    private static Command BuildVersionsRoot()
    {
        var apiUrl = new Option<string>("--api-url", () => "https://test");
        var token = new Option<string?>("--token");
        var output = new Option<string>("--output", () => "table");
        var versions = new Command("versions", "Manage policy versions");
        VersionCommands.Register(versions, apiUrl, token, output);
        return versions;
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("wind-down")]
    [InlineData("retire")]
    public void Versions_RegistersLifecycleVerb(string verb)
    {
        var versions = BuildVersionsRoot();

        var verbCmd = versions.Subcommands.FirstOrDefault(c => c.Name == verb);
        verbCmd.Should().NotBeNull($"versions {verb} should be registered");
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("wind-down")]
    [InlineData("retire")]
    public void LifecycleVerb_HasPolicyAndVersionArgs(string verb)
    {
        var versions = BuildVersionsRoot();
        var verbCmd = versions.Subcommands.First(c => c.Name == verb);

        verbCmd.Arguments.Should().HaveCount(2);
        verbCmd.Arguments.Select(a => a.Name).Should()
            .ContainInOrder("policyIdOrName", "versionId");
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("wind-down")]
    [InlineData("retire")]
    public void LifecycleVerb_HasRationaleOption_WithShortAlias(string verb)
    {
        var versions = BuildVersionsRoot();
        var verbCmd = versions.Subcommands.First(c => c.Name == verb);

        var rationale = verbCmd.Options.FirstOrDefault(o => o.Name == "rationale");
        rationale.Should().NotBeNull("--rationale must be defined");
        rationale!.Aliases.Should().Contain("-r");
        rationale.IsRequired.Should().BeFalse(
            "rationale is server-side enforced via andy.policies.rationaleRequired (P2.4)");
    }

    [Theory]
    [InlineData("publish", "Draft -> Active")]
    [InlineData("wind-down", "Active -> WindingDown")]
    [InlineData("retire", "-> Retired")]
    public void LifecycleVerb_HasDescriptiveHelp(string verb, string expectedFragment)
    {
        var versions = BuildVersionsRoot();
        var verbCmd = versions.Subcommands.First(c => c.Name == verb);

        verbCmd.Description.Should().Contain(expectedFragment);
    }
}
