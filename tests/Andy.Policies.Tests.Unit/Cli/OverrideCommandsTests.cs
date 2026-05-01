// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using Andy.Policies.Cli.Commands;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Cli;

/// <summary>
/// Command-tree + parser tests for P5.7 (#60). Asserts that the
/// <c>overrides</c> noun gets the six subcommands (<c>propose</c>,
/// <c>approve</c>, <c>revoke</c>, <c>list</c>, <c>get</c>,
/// <c>active</c>) with the expected required flags, and exercises
/// the <see cref="OverrideCommands.ParseExpiresAt"/> helper across
/// ISO 8601 and relative-duration shapes (<c>+30d</c>, <c>+8h</c>,
/// <c>+45m</c>) — the API has a <c>+1m</c> server-side floor; this
/// is the CLI-side belt that fail-fasts past values without round-
/// tripping to the server.
/// </summary>
public class OverrideCommandsTests
{
    private static Command BuildOverridesRoot()
    {
        var apiUrl = new Option<string>("--api-url", () => "https://test");
        var token = new Option<string?>("--token");
        var output = new Option<string>("--output", () => "table");
        var root = new Command("overrides", "Manage policy overrides");
        OverrideCommands.Register(root, apiUrl, token, output);
        return root;
    }

    [Theory]
    [InlineData("propose")]
    [InlineData("approve")]
    [InlineData("revoke")]
    [InlineData("list")]
    [InlineData("get")]
    [InlineData("active")]
    public void Overrides_RegistersSubcommand(string subcommand)
    {
        var root = BuildOverridesRoot();

        root.Subcommands.Select(c => c.Name).Should().Contain(subcommand);
    }

    [Fact]
    public void Propose_RequiresAllNonOptionalFlags()
    {
        var root = BuildOverridesRoot();
        var propose = root.Subcommands.First(c => c.Name == "propose");

        var required = propose.Options.Where(o => o.IsRequired).Select(o => o.Name).ToList();
        required.Should().Contain(new[]
        {
            "policy-version-id", "scope-kind", "scope-ref", "effect",
            "expires-at", "rationale",
        });
        // --replacement-policy-version-id is optional (only required when
        // --effect=Replace; the API enforces the invariant).
        var replacement = propose.Options.FirstOrDefault(o => o.Name == "replacement-policy-version-id");
        replacement.Should().NotBeNull();
        replacement!.IsRequired.Should().BeFalse();
    }

    [Fact]
    public void Revoke_RequiresReason()
    {
        var root = BuildOverridesRoot();
        var revoke = root.Subcommands.First(c => c.Name == "revoke");

        revoke.Options.Where(o => o.IsRequired).Select(o => o.Name)
            .Should().Contain("reason");
    }

    [Fact]
    public void Active_RequiresBothScopeFlags()
    {
        var root = BuildOverridesRoot();
        var active = root.Subcommands.First(c => c.Name == "active");

        active.Options.Where(o => o.IsRequired).Select(o => o.Name)
            .Should().Contain(new[] { "scope-kind", "scope-ref" });
    }

    [Fact]
    public void List_TakesOptionalFilters_NoneRequired()
    {
        var root = BuildOverridesRoot();
        var list = root.Subcommands.First(c => c.Name == "list");

        list.Options.Where(o => o.IsRequired).Should().BeEmpty();
        list.Options.Select(o => o.Name).Should().Contain(new[]
        {
            "state", "scope-kind", "scope-ref", "policy-version-id",
        });
    }

    // ----- ParseExpiresAt -----------------------------------------------

    [Fact]
    public void ParseExpiresAt_RelativeDays_AddsToNow()
    {
        var now = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

        var result = OverrideCommands.ParseExpiresAt("+30d", now);

        result.Should().Be(now.AddDays(30));
    }

    [Fact]
    public void ParseExpiresAt_RelativeHours_AddsToNow()
    {
        var now = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

        var result = OverrideCommands.ParseExpiresAt("+8h", now);

        result.Should().Be(now.AddHours(8));
    }

    [Fact]
    public void ParseExpiresAt_RelativeMinutes_AddsToNow()
    {
        var now = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

        var result = OverrideCommands.ParseExpiresAt("+45m", now);

        result.Should().Be(now.AddMinutes(45));
    }

    [Fact]
    public void ParseExpiresAt_Iso8601Z_ReturnsUtc()
    {
        var now = DateTimeOffset.UtcNow;

        var result = OverrideCommands.ParseExpiresAt("2026-05-01T00:00:00Z", now);

        result.Should().Be(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+abc")]
    [InlineData("+0d")]
    [InlineData("-5d")]
    [InlineData("+5y")]
    public void ParseExpiresAt_InvalidShape_ReturnsNull(string raw)
    {
        var now = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

        var result = OverrideCommands.ParseExpiresAt(raw, now);

        result.Should().BeNull();
    }
}
