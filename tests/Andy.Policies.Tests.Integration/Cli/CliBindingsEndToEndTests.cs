// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Cli.Commands;
using Andy.Policies.Cli.Http;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Andy.Policies.Tests.Integration.Cli;

/// <summary>
/// Full-stack CLI tests for P3.7 (#25). Boots the API via
/// <see cref="PoliciesApiFactory"/>, swaps the CLI's HTTP handler with
/// the test server's via the internal
/// <see cref="ClientFactory.UseHandlerForTesting"/> seam, and drives
/// <c>bindings {list,create,delete,resolve}</c> through
/// <see cref="Command.InvokeAsync"/>. Verifies that the CLI lands on the
/// REST surface from P3.3/P3.4 with the expected exit codes and response
/// bodies.
/// </summary>
public class CliBindingsEndToEndTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;

    /// <summary>
    /// API serializes enums as strings via the global
    /// <see cref="JsonStringEnumConverter"/> registered in Program.cs.
    /// Tests need the same converter to deserialize <see cref="BindingDto"/>
    /// back into <see cref="Andy.Policies.Domain.Enums.BindingTargetType"/> /
    /// <see cref="Andy.Policies.Domain.Enums.BindStrength"/>.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public CliBindingsEndToEndTests(PoliciesApiFactory factory)
    {
        _factory = factory;
    }

    private Command BuildRootCommand()
    {
        var apiUrl = new Option<string>("--api-url", () => _factory.Server.BaseAddress.ToString());
        var token = new Option<string?>("--token");
        var output = new Option<string>("--output", () => "json");

        var root = new RootCommand("test-cli");
        root.AddGlobalOption(apiUrl);
        root.AddGlobalOption(token);
        root.AddGlobalOption(output);
        var bindings = new Command("bindings", "Manage bindings");
        BindingCommands.Register(bindings, apiUrl, token, output);
        root.AddCommand(bindings);
        return root;
    }

    private async Task<PolicyVersionDto> CreateDraftAsync(string slug)
    {
        var http = _factory.CreateClient();
        var resp = await http.PostAsJsonAsync("/api/policies", new
        {
            name = slug,
            description = (string?)null,
            summary = "summary",
            enforcement = "Must",
            severity = "Critical",
            scopes = Array.Empty<string>(),
            rulesJson = "{}",
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
    }

    private static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}".Substring(0, 16);

    [Fact]
    public async Task List_WithoutAnyFilter_ReturnsBadArgumentsExit()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[] { "bindings", "list" });

        // ExitCodes.BadArguments = 2
        exit.Should().Be(2);
    }

    [Fact]
    public async Task Create_HappyPath_ReturnsZero_AndPersistsBinding()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var draft = await CreateDraftAsync(Slug("cli-bind-cre"));
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "bindings", "create",
            "--policy-version-id", draft.Id.ToString(),
            "--target-type", "Repo",
            "--target-ref", "repo:rivoli-ai/cli",
            "--bind-strength", "Mandatory",
        });

        exit.Should().Be(0);
        // Confirm via REST.
        var http = _factory.CreateClient();
        var resp = await http.GetAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/bindings");
        var rows = await resp.Content.ReadFromJsonAsync<List<BindingDto>>(JsonOptions);
        rows.Should().NotBeNull().And.NotBeEmpty();
    }

    [Fact]
    public async Task List_ByPolicyVersionId_ReturnsZero()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var draft = await CreateDraftAsync(Slug("cli-bind-list"));
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "bindings", "list",
            "--policy-version-id", draft.Id.ToString(),
        });

        exit.Should().Be(0);
    }

    [Fact]
    public async Task List_ByTarget_ReturnsZero_OnEmptyResultSet()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "bindings", "list",
            "--target-type", "Repo",
            "--target-ref", $"repo:none/missing-{Guid.NewGuid():N}",
        });

        exit.Should().Be(0);
    }

    [Fact]
    public async Task Delete_RoundTrips_AndSecondDeleteReturnsNotFound()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var draft = await CreateDraftAsync(Slug("cli-bind-del"));
        var http = _factory.CreateClient();
        var created = await http.PostAsJsonAsync("/api/bindings", new
        {
            policyVersionId = draft.Id,
            targetType = "Repo",
            targetRef = "repo:cli/del",
            bindStrength = "Mandatory",
        });
        var binding = (await created.Content.ReadFromJsonAsync<BindingDto>(JsonOptions))!;
        var root = BuildRootCommand();

        var first = await root.InvokeAsync(new[]
        {
            "bindings", "delete", binding.Id.ToString(),
            "-r", "no longer needed",
        });
        first.Should().Be(0);

        var second = await root.InvokeAsync(new[]
        {
            "bindings", "delete", binding.Id.ToString(),
        });
        // ExitCodes.NotFound = 4
        second.Should().Be(4);
    }

    [Fact]
    public async Task Resolve_HappyPath_ReturnsZero()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var draft = await CreateDraftAsync(Slug("cli-bind-res"));
        var http = _factory.CreateClient();
        var target = $"template:{Guid.NewGuid()}";
        await http.PostAsJsonAsync("/api/bindings", new
        {
            policyVersionId = draft.Id,
            targetType = "Template",
            targetRef = target,
            bindStrength = "Mandatory",
        });
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "bindings", "resolve",
            "--target-type", "Template",
            "--target-ref", target,
        });

        exit.Should().Be(0);
    }

    [Fact]
    public async Task Create_OnUnknownVersion_ReturnsNotFoundExit()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "bindings", "create",
            "--policy-version-id", Guid.NewGuid().ToString(),
            "--target-type", "Repo",
            "--target-ref", "repo:any/repo",
            "--bind-strength", "Mandatory",
        });

        // ExitCodes.NotFound = 4
        exit.Should().Be(4);
    }
}
