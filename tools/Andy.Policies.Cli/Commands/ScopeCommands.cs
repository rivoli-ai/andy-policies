// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.Net.Http.Json;
using Andy.Policies.Cli.Http;
using Andy.Policies.Cli.Output;

namespace Andy.Policies.Cli.Commands;

/// <summary>
/// <c>andy-policies-cli scopes {list,get,tree,create,delete,effective}</c>
/// (P4.6, story rivoli-ai/andy-policies#34). Mirrors the scope REST
/// surface from P4.5: <c>list</c> takes optional <c>--type</c>;
/// <c>get</c> / <c>delete</c> / <c>effective</c> take a positional
/// node id; <c>create</c> takes parent / type / ref / display-name;
/// <c>tree</c> dumps the full forest as JSON. Same exit-code contract
/// as the binding CLI: 0 success, 2 bad args, 3 auth, 4 not found,
/// 5 conflict, 1 transport.
/// </summary>
internal static class ScopeCommands
{
    public static void Register(
        Command parent,
        Option<string> apiUrlOption,
        Option<string?> tokenOption,
        Option<string> outputOption)
    {
        parent.AddCommand(BuildList(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildGet(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildTree(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildCreate(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildDelete(apiUrlOption, tokenOption));
        parent.AddCommand(BuildEffective(apiUrlOption, tokenOption, outputOption));
    }

    private static Command BuildList(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("list", "List scope nodes; optional --type filter.");
        var typeOpt = new Option<string?>(
            aliases: new[] { "--type" },
            description: "Filter by ScopeType (Org/Tenant/Team/Repo/Template/Run).");
        command.AddOption(typeOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var type = ctx.ParseResult.GetValueForOption(typeOpt);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var url = string.IsNullOrEmpty(type) ? "/api/scopes" : $"/api/scopes?type={Uri.EscapeDataString(type)}";
            var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OutputRenderer.Write(body, fmt, new[] { "id", "type", "ref", "displayName", "depth" });
        });
        return command;
    }

    private static Command BuildGet(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("get", "Get a single scope node by id.");
        var idArg = new Argument<Guid>("id", "Scope node id (GUID).");
        command.AddArgument(idArg);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync($"/api/scopes/{id}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OutputRenderer.Write(body, fmt);
        });
        return command;
    }

    private static Command BuildTree(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("tree", "Return the full scope forest as nested JSON.");

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync("/api/scopes/tree", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OutputRenderer.Write(body, fmt);
        });
        return command;
    }

    private static Command BuildCreate(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("create", "Create a new scope node (root Org or child of an existing parent).");
        var parentOpt = new Option<Guid?>(
            aliases: new[] { "--parent" },
            description: "Parent scope id (GUID). Omit for root Org.");
        var typeOpt = new Option<string>(
            aliases: new[] { "--type" },
            description: "Scope type (Org/Tenant/Team/Repo/Template/Run).") { IsRequired = true };
        var refOpt = new Option<string>(
            aliases: new[] { "--ref" },
            description: "Scope reference (e.g. 'org:rivoli', 'repo:rivoli-ai/conductor').") { IsRequired = true };
        var displayOpt = new Option<string>(
            aliases: new[] { "--display-name", "-n" },
            description: "Human-readable display name.") { IsRequired = true };
        command.AddOption(parentOpt);
        command.AddOption(typeOpt);
        command.AddOption(refOpt);
        command.AddOption(displayOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var parent = ctx.ParseResult.GetValueForOption(parentOpt);
            var type = ctx.ParseResult.GetValueForOption(typeOpt)!;
            var refValue = ctx.ParseResult.GetValueForOption(refOpt)!;
            var displayName = ctx.ParseResult.GetValueForOption(displayOpt)!;
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.PostAsJsonAsync(
                "/api/scopes",
                new { parentId = parent, type, @ref = refValue, displayName },
                ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OutputRenderer.Write(body, fmt);
        });
        return command;
    }

    private static Command BuildDelete(Option<string> apiUrl, Option<string?> token)
    {
        var command = new Command("delete", "Delete a leaf scope node (refused with non-zero exit if it has descendants).");
        var idArg = new Argument<Guid>("id", "Scope node id (GUID).");
        command.AddArgument(idArg);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.DeleteAsync($"/api/scopes/{id}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            // 204 No Content on success.
        });
        return command;
    }

    private static Command BuildEffective(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("effective", "Resolve the effective policy set for a scope (P4.3 tighten-only fold).");
        var idArg = new Argument<Guid>("id", "Scope node id (GUID).");
        command.AddArgument(idArg);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync($"/api/scopes/{id}/effective-policies", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OutputRenderer.Write(body, fmt);
        });
        return command;
    }
}
