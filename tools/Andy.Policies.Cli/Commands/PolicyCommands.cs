// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.Text.Json;
using Andy.Policies.Cli.Http;
using Andy.Policies.Cli.Output;

namespace Andy.Policies.Cli.Commands;

/// <summary>
/// <c>andy-policies-cli policies {list,get,show,active}</c> — REST-backed
/// policy-catalogue commands per P1.8 (rivoli-ai/andy-policies#78). Every
/// command goes through the public REST surface (<see cref="ClientFactory"/>),
/// never the service layer directly: the CLI is shipped as a standalone
/// binary that targets remote API instances.
/// </summary>
internal static class PolicyCommands
{
    public static void Register(
        Command parent,
        Option<string> apiUrlOption,
        Option<string?> tokenOption,
        Option<string> outputOption)
    {
        parent.AddCommand(BuildList(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildGet(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildShow(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildActive(apiUrlOption, tokenOption, outputOption));
    }

    private static Command BuildList(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("list", "List policies");
        var nameOpt = new Option<string?>("--name-prefix", "Filter by policy name prefix");
        var scopeOpt = new Option<string?>("--scope", "Filter by scope tag");
        var enfOpt = new Option<string?>("--enforcement", "may | should | must");
        var sevOpt = new Option<string?>("--severity", "info | moderate | critical");
        var skipOpt = new Option<int>("--skip", () => 0, "Pagination offset");
        var takeOpt = new Option<int>("--take", () => 100, "Page size (max enforced server-side)");
        command.AddOption(nameOpt);
        command.AddOption(scopeOpt);
        command.AddOption(enfOpt);
        command.AddOption(sevOpt);
        command.AddOption(skipOpt);
        command.AddOption(takeOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var qs = Querystring.Build(
                ("namePrefix", ctx.ParseResult.GetValueForOption(nameOpt)),
                ("scope", ctx.ParseResult.GetValueForOption(scopeOpt)),
                ("enforcement", ctx.ParseResult.GetValueForOption(enfOpt)),
                ("severity", ctx.ParseResult.GetValueForOption(sevOpt)),
                ("skip", ctx.ParseResult.GetValueForOption(skipOpt).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("take", ctx.ParseResult.GetValueForOption(takeOpt).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var resp = await http.GetAsync($"/api/policies{qs}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OutputRenderer.Write(body, fmt, new[] { "id", "name", "versionCount", "activeVersionId" });
        });
        return command;
    }

    private static Command BuildGet(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("get", "Get a policy by id (GUID) or name slug");
        var idArg = new Argument<string>("idOrName", "Policy id (GUID) or name slug");
        command.AddArgument(idArg);
        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var idOrName = ctx.ParseResult.GetValueForArgument(idArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync(BuildPolicyRoute(idOrName), ct).ConfigureAwait(false);
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

    private static Command BuildShow(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("show", "Show a policy plus all its versions");
        var idArg = new Argument<string>("idOrName", "Policy id (GUID) or name slug");
        command.AddArgument(idArg);
        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var idOrName = ctx.ParseResult.GetValueForArgument(idArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var policyResp = await http.GetAsync(BuildPolicyRoute(idOrName), ct).ConfigureAwait(false);
            if (!policyResp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(policyResp, ct).ConfigureAwait(false);
                return;
            }
            var policyBody = await policyResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var policyDoc = JsonDocument.Parse(policyBody);
            if (!policyDoc.RootElement.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            {
                await Console.Error.WriteLineAsync("Unexpected policy payload (missing id).").ConfigureAwait(false);
                ctx.ExitCode = ExitCodes.Transport;
                return;
            }
            var policyId = idProp.GetString();
            var versionsResp = await http.GetAsync($"/api/policies/{Uri.EscapeDataString(policyId!)}/versions", ct).ConfigureAwait(false);
            if (!versionsResp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(versionsResp, ct).ConfigureAwait(false);
                return;
            }
            var versionsBody = await versionsResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (fmt == "json" || fmt == "yaml")
            {
                var combined = $"{{\"policy\":{policyBody},\"versions\":{versionsBody}}}";
                OutputRenderer.Write(combined, fmt);
            }
            else
            {
                Console.WriteLine("Policy");
                OutputRenderer.Write(policyBody, "table");
                Console.WriteLine();
                Console.WriteLine("Versions");
                OutputRenderer.Write(versionsBody, "table", new[] { "version", "state", "enforcement", "severity", "id" });
            }
        });
        return command;
    }

    private static Command BuildActive(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("active", "Show the active version of a policy");
        var idArg = new Argument<string>("idOrName", "Policy id (GUID) or name slug");
        command.AddArgument(idArg);
        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var idOrName = ctx.ParseResult.GetValueForArgument(idArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var policyResp = await http.GetAsync(BuildPolicyRoute(idOrName), ct).ConfigureAwait(false);
            if (!policyResp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(policyResp, ct).ConfigureAwait(false);
                return;
            }
            var policyBody = await policyResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var policyDoc = JsonDocument.Parse(policyBody);
            if (!policyDoc.RootElement.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            {
                await Console.Error.WriteLineAsync("Unexpected policy payload (missing id).").ConfigureAwait(false);
                ctx.ExitCode = ExitCodes.Transport;
                return;
            }
            var policyId = idProp.GetString();
            var resp = await http.GetAsync($"/api/policies/{Uri.EscapeDataString(policyId!)}/versions/active", ct).ConfigureAwait(false);
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

    internal static string BuildPolicyRoute(string idOrName)
    {
        return Guid.TryParse(idOrName, out var guid)
            ? $"/api/policies/{guid}"
            : $"/api/policies/by-name/{Uri.EscapeDataString(idOrName)}";
    }
}
