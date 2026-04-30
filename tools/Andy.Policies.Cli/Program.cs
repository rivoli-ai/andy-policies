// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using Andy.Policies.Cli.Commands;

namespace Andy.Policies.Cli;

/// <summary>
/// Entry point for <c>andy-policies-cli</c>. Lives in the
/// <c>Andy.Policies.Cli</c> namespace (rather than the implicit global
/// top-level-statements form) so the test-integration project can reference
/// both <c>Andy.Policies.Cli</c> and <c>Andy.Policies.Api</c> without their
/// global <c>Program</c> classes colliding (the API's
/// <c>WebApplicationFactory&lt;Program&gt;</c> still resolves to the global
/// type emitted by its top-level statements).
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Andy Policies CLI - Manage andy-policies resources");

        var apiUrlOption = new Option<string>(
            "--api-url",
            getDefaultValue: () => "https://localhost:5112",
            description: "The Andy Policies API base URL");
        rootCommand.AddGlobalOption(apiUrlOption);

        var tokenOption = new Option<string?>(
            "--token",
            description: "Bearer token for authentication");
        rootCommand.AddGlobalOption(tokenOption);

        var outputOption = new Option<string>(
            "--output",
            getDefaultValue: () => "table",
            description: "Output format: table | json | yaml");
        outputOption.FromAmong("table", "json", "yaml");
        rootCommand.AddGlobalOption(outputOption);

        var noColorOption = new Option<bool>(
            "--no-color",
            getDefaultValue: () =>
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                || Environment.GetEnvironmentVariable("TERM") == "dumb",
            description: "Disable ANSI color in output");
        rootCommand.AddGlobalOption(noColorOption);

        // Item commands (template scaffolding — kept for backward compat)
        var itemsCommand = new Command("items", "Manage items");
        ItemCommands.Register(itemsCommand, apiUrlOption, tokenOption);
        rootCommand.AddCommand(itemsCommand);

        var policiesCommand = new Command("policies", "Manage the policy catalogue");
        PolicyCommands.Register(policiesCommand, apiUrlOption, tokenOption, outputOption);
        rootCommand.AddCommand(policiesCommand);

        var versionsCommand = new Command("versions", "Manage policy versions");
        VersionCommands.Register(versionsCommand, apiUrlOption, tokenOption, outputOption);
        rootCommand.AddCommand(versionsCommand);

        var bindingsCommand = new Command("bindings", "Manage policy bindings");
        BindingCommands.Register(bindingsCommand, apiUrlOption, tokenOption, outputOption);
        rootCommand.AddCommand(bindingsCommand);

        var scopesCommand = new Command("scopes", "Manage the scope hierarchy");
        ScopeCommands.Register(scopesCommand, apiUrlOption, tokenOption, outputOption);
        rootCommand.AddCommand(scopesCommand);

        return await rootCommand.InvokeAsync(args);
    }
}
