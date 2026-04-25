// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using Andy.Policies.Cli.Commands;

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

return await rootCommand.InvokeAsync(args);
