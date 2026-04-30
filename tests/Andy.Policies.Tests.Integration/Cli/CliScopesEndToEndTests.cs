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
/// Full-stack CLI tests for P4.6 (#34). Boots the API via
/// <see cref="PoliciesApiFactory"/>, swaps the CLI's HTTP handler with
/// the test server's via the internal
/// <see cref="ClientFactory.UseHandlerForTesting"/> seam, and drives
/// <c>scopes {list,get,tree,create,delete,effective}</c> through
/// <see cref="Command.InvokeAsync"/>.
/// </summary>
public class CliScopesEndToEndTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public CliScopesEndToEndTests(PoliciesApiFactory factory)
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
        var scopes = new Command("scopes", "Manage scopes");
        ScopeCommands.Register(scopes, apiUrl, token, output);
        root.AddCommand(scopes);
        return root;
    }

    private async Task<ScopeNodeDto> CreateOrgAsync(string @ref)
    {
        var http = _factory.CreateClient();
        var resp = await http.PostAsJsonAsync("/api/scopes", new
        {
            parentId = (Guid?)null,
            type = "Org",
            @ref,
            displayName = "Test Org",
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ScopeNodeDto>(JsonOptions))!;
    }

    private static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}".Substring(0, 20);

    [Fact]
    public async Task Create_HappyPath_ReturnsZero_AndPersistsScope()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var orgRef = Slug("org:cli-create");
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "scopes", "create",
            "--type", "Org",
            "--ref", orgRef,
            "--display-name", "Test",
        });

        exit.Should().Be(0);
        var http = _factory.CreateClient();
        var rows = await http.GetFromJsonAsync<List<ScopeNodeDto>>("/api/scopes?type=Org", JsonOptions);
        rows.Should().Contain(n => n.Ref == orgRef);
    }

    [Fact]
    public async Task List_OnEmptyFilter_ReturnsZero()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[] { "scopes", "list" });

        exit.Should().Be(0);
    }

    [Fact]
    public async Task Get_OnUnknownId_ReturnsNotFoundExit()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "scopes", "get", Guid.NewGuid().ToString(),
        });

        // ExitCodes.NotFound = 4
        exit.Should().Be(4);
    }

    [Fact]
    public async Task Tree_ReturnsZero()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[] { "scopes", "tree" });

        exit.Should().Be(0);
    }

    [Fact]
    public async Task Delete_OnLeaf_ReturnsZero_AndSecondDeleteReturnsNotFound()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var dto = await CreateOrgAsync(Slug("org:cli-del"));
        var root = BuildRootCommand();

        (await root.InvokeAsync(new[]
        {
            "scopes", "delete", dto.Id.ToString(),
        })).Should().Be(0);

        // ExitCodes.NotFound = 4
        (await root.InvokeAsync(new[]
        {
            "scopes", "delete", dto.Id.ToString(),
        })).Should().Be(4);
    }

    [Fact]
    public async Task Delete_OnNonLeaf_ReturnsConflictExit()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var http = _factory.CreateClient();
        var orgResp = await http.PostAsJsonAsync("/api/scopes", new
        {
            parentId = (Guid?)null,
            type = "Org",
            @ref = Slug("org:cli-non"),
            displayName = "Org",
        });
        var org = (await orgResp.Content.ReadFromJsonAsync<ScopeNodeDto>(JsonOptions))!;
        await http.PostAsJsonAsync("/api/scopes", new
        {
            parentId = org.Id,
            type = "Tenant",
            @ref = Slug("tenant:cli-non"),
            displayName = "Tn",
        });
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "scopes", "delete", org.Id.ToString(),
        });

        // ExitCodes.Conflict = 5
        exit.Should().Be(5);
    }

    [Fact]
    public async Task Effective_HappyPath_ReturnsZero()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var dto = await CreateOrgAsync(Slug("org:cli-eff"));
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "scopes", "effective", dto.Id.ToString(),
        });

        exit.Should().Be(0);
    }

    [Fact]
    public async Task Create_WithLadderViolation_ReturnsBadRequestExit()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var org = await CreateOrgAsync(Slug("org:cli-vio"));
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "scopes", "create",
            "--parent", org.Id.ToString(),
            "--type", "Team",  // Team's parent must be Tenant, not Org.
            "--ref", Slug("team:wrong"),
            "--display-name", "Bad",
        });

        // ExitCodes maps 400 to Transport (1) — the federated CLI
        // contract reserves 2 for parser errors and uses 1 for any
        // generic non-2xx that doesn't fit 3/4/5. The test asserts a
        // non-zero exit.
        exit.Should().NotBe(0);
    }
}
