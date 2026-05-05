// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.Net.Http.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Cli.Commands;
using Andy.Policies.Cli.Http;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Andy.Policies.Tests.Integration.Cli;

/// <summary>
/// Full-stack CLI tests for P2.7 (#17). Boots the API via
/// <see cref="PoliciesApiFactory"/>, swaps the CLI's HTTP handler with the
/// test server's via the internal <see cref="ClientFactory.UseHandlerForTesting"/>
/// seam, and drives <c>versions {publish,wind-down,retire}</c> through
/// <see cref="Command.InvokeAsync"/>. Verifies that the CLI lands on the
/// REST surface from P2.3 with the expected exit codes and DB state.
/// </summary>
public class CliLifecycleEndToEndTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;

    public CliLifecycleEndToEndTests(PoliciesApiFactory factory)
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
        var versions = new Command("versions", "Manage policy versions");
        VersionCommands.Register(versions, apiUrl, token, output);
        root.AddCommand(versions);
        return root;
    }

    private async Task<PolicyVersionDto> CreateDraftAsync(string slug)
    {
        // P7.3 (#55): pin the proposer to "test-creator" so the CLI's
        // publish call (which goes through TestAuthHandler with the
        // default subject "test-user") doesn't trip the self-approval
        // guard.
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsSubjectAsync("/api/policies", new
        {
            name = slug,
            description = (string?)null,
            summary = "summary",
            enforcement = "Must",
            severity = "Critical",
            scopes = Array.Empty<string>(),
            rulesJson = "{}",
        }, "test-creator");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
    }

    [Fact]
    public async Task PublishCommand_OnDraft_ReturnsExitZero_AndFlipsStateToActive()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var draft = await CreateDraftAsync($"cli-publish-{Guid.NewGuid():N}".Substring(0, 16));

        var root = BuildRootCommand();
        var exit = await root.InvokeAsync(new[]
        {
            "versions", "publish",
            draft.PolicyId.ToString(),
            draft.Id.ToString(),
            "--rationale", "ship-it",
        });

        exit.Should().Be(0);

        var client = _factory.CreateClient();
        var reloaded = await client.GetFromJsonAsync<PolicyVersionDto>(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}");
        reloaded!.State.Should().Be("Active");
    }

    [Fact]
    public async Task PublishCommand_WithEmptyRationale_ReturnsTransport_FromBadRequest()
    {
        // ExitCodes.FromStatus maps anything outside { 401/403/404/409/412/2xx }
        // to ExitCodes.Transport (1). 400 is one of those — the CLI surfaces a
        // generic "request failed" instead of a typed "bad arguments" code
        // because System.CommandLine reserves exit 2 for parser-level errors.
        // The acceptance criterion is that the binary exits non-zero and the
        // DB stays untouched.
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var draft = await CreateDraftAsync($"cli-norat-{Guid.NewGuid():N}".Substring(0, 16));

        var root = BuildRootCommand();
        var exit = await root.InvokeAsync(new[]
        {
            "versions", "publish",
            draft.PolicyId.ToString(),
            draft.Id.ToString(),
        });

        exit.Should().Be(1);

        var client = _factory.CreateClient();
        var reloaded = await client.GetFromJsonAsync<PolicyVersionDto>(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}");
        reloaded!.State.Should().Be("Draft");
    }

    [Fact]
    public async Task LifecycleVerbs_Round_Trip_DraftToRetired()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var draft = await CreateDraftAsync($"cli-rt-{Guid.NewGuid():N}".Substring(0, 16));
        var root = BuildRootCommand();

        (await root.InvokeAsync(new[]
        {
            "versions", "publish", draft.PolicyId.ToString(), draft.Id.ToString(), "-r", "live",
        })).Should().Be(0);

        (await root.InvokeAsync(new[]
        {
            "versions", "wind-down", draft.PolicyId.ToString(), draft.Id.ToString(), "-r", "sunset",
        })).Should().Be(0);

        (await root.InvokeAsync(new[]
        {
            "versions", "retire", draft.PolicyId.ToString(), draft.Id.ToString(), "-r", "tomb",
        })).Should().Be(0);

        var client = _factory.CreateClient();
        var reloaded = await client.GetFromJsonAsync<PolicyVersionDto>(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}");
        reloaded!.State.Should().Be("Retired");
    }

    [Fact]
    public async Task PublishCommand_OnUnknownIds_ReturnsNotFoundExit()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "versions", "publish",
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            "-r", "go",
        });

        // ExitCodes.NotFound = 4
        exit.Should().Be(4);
    }

    [Fact]
    public async Task RetireCommand_OnDraft_ReturnsConflictExit_FromInvalidTransition()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var draft = await CreateDraftAsync($"cli-bad-{Guid.NewGuid():N}".Substring(0, 16));
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "versions", "retire",
            draft.PolicyId.ToString(),
            draft.Id.ToString(),
            "-r", "skip",
        });

        // ExitCodes.Conflict = 5; Draft -> Retired isn't in the matrix.
        exit.Should().Be(5);
    }

    [Fact]
    public async Task PublishCommand_AcceptsPolicyNameSlug_NotJustGuid()
    {
        using var _scope = ClientFactory.UseHandlerForTesting(_factory.Server.CreateHandler());
        var slug = $"cli-slug-{Guid.NewGuid():N}".Substring(0, 16);
        var draft = await CreateDraftAsync(slug);
        var root = BuildRootCommand();

        var exit = await root.InvokeAsync(new[]
        {
            "versions", "publish",
            slug,                       // name slug, not GUID
            draft.Id.ToString(),
            "-r", "ship",
        });

        exit.Should().Be(0);
    }
}
