// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using Andy.Policies.Cli.Commands;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Cli;

/// <summary>
/// Command-tree shape tests for P3.7 (#25). Asserts that the
/// <c>bindings</c> noun gets <c>list</c>, <c>create</c>, <c>delete</c>,
/// <c>resolve</c> subcommands, each with the expected positional args
/// and option flags. Catches accidental rename or option-removal during
/// refactors before integration tests run.
/// </summary>
public class BindingCommandsTests
{
    private static Command BuildBindingsRoot()
    {
        var apiUrl = new Option<string>("--api-url", () => "https://test");
        var token = new Option<string?>("--token");
        var output = new Option<string>("--output", () => "table");
        var bindings = new Command("bindings", "Manage policy bindings");
        BindingCommands.Register(bindings, apiUrl, token, output);
        return bindings;
    }

    [Theory]
    [InlineData("list")]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("resolve")]
    public void Bindings_RegistersSubcommand(string subcommand)
    {
        var bindings = BuildBindingsRoot();

        bindings.Subcommands.Select(c => c.Name).Should().Contain(subcommand);
    }

    [Fact]
    public void List_HasFilterOptions_ButNoRequiredOption()
    {
        // list takes either --policy-version-id OR (--target-type +
        // --target-ref). Neither is marked required at the parser layer
        // because the handler enforces the OR constraint dynamically.
        var bindings = BuildBindingsRoot();
        var list = bindings.Subcommands.First(c => c.Name == "list");

        list.Options.Select(o => o.Name).Should().Contain(new[]
        {
            "policy-version-id", "target-type", "target-ref", "include-deleted",
        });
        list.Options.Where(o => o.IsRequired).Should().BeEmpty();
    }

    [Fact]
    public void Create_RequiresPolicyVersionAndTargetOptions()
    {
        var bindings = BuildBindingsRoot();
        var create = bindings.Subcommands.First(c => c.Name == "create");

        var requiredNames = create.Options.Where(o => o.IsRequired).Select(o => o.Name).ToList();
        requiredNames.Should().Contain(new[]
        {
            "policy-version-id", "target-type", "target-ref",
        });
        // bind-strength has a default and isn't required.
        var bindStrength = create.Options.FirstOrDefault(o => o.Name == "bind-strength");
        bindStrength.Should().NotBeNull();
        bindStrength!.IsRequired.Should().BeFalse();
    }

    [Fact]
    public void Delete_TakesPositionalBindingIdArgument_AndRationaleOption()
    {
        var bindings = BuildBindingsRoot();
        var delete = bindings.Subcommands.First(c => c.Name == "delete");

        delete.Arguments.Should().ContainSingle().Which.Name.Should().Be("bindingId");
        var rationale = delete.Options.FirstOrDefault(o => o.Name == "rationale");
        rationale.Should().NotBeNull();
        rationale!.Aliases.Should().Contain("-r");
    }

    [Fact]
    public void Resolve_RequiresTargetTypeAndTargetRef()
    {
        var bindings = BuildBindingsRoot();
        var resolve = bindings.Subcommands.First(c => c.Name == "resolve");

        var requiredNames = resolve.Options.Where(o => o.IsRequired).Select(o => o.Name).ToList();
        requiredNames.Should().Contain(new[] { "target-type", "target-ref" });
    }
}
