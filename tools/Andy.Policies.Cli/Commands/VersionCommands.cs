// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Cli.Http;
using Andy.Policies.Cli.Output;

namespace Andy.Policies.Cli.Commands;

/// <summary>
/// <c>andy-policies-cli versions {list,get,draft-new,draft-edit,draft-bump}</c>
/// per P1.8 (rivoli-ai/andy-policies#78). Mirrors the REST endpoints exposed by
/// <c>PoliciesController</c>: <c>GET</c> for read; <c>POST /api/policies</c> for
/// draft-new; <c>PUT /api/policies/{id}/versions/{vid}</c> for draft-edit;
/// <c>POST /api/policies/{id}/versions/{srcVid}/bump</c> for draft-bump.
/// </summary>
internal static class VersionCommands
{
    public static void Register(
        Command parent,
        Option<string> apiUrlOption,
        Option<string?> tokenOption,
        Option<string> outputOption)
    {
        parent.AddCommand(BuildList(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildGet(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildDraftNew(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildDraftEdit(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildDraftBump(apiUrlOption, tokenOption, outputOption));
    }

    private static Command BuildList(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("list", "List versions of a policy");
        var policyArg = new Argument<string>("policyIdOrName", "Policy id (GUID) or name slug");
        command.AddArgument(policyArg);
        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var policyIdOrName = ctx.ParseResult.GetValueForArgument(policyArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var policyId = await ResolvePolicyIdAsync(http, policyIdOrName, ctx, ct).ConfigureAwait(false);
            if (policyId is null)
            {
                return;
            }

            var resp = await http.GetAsync($"/api/policies/{policyId}/versions", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OutputRenderer.Write(body, fmt, new[] { "version", "state", "enforcement", "severity", "id" });
        });
        return command;
    }

    private static Command BuildGet(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("get", "Get a specific policy version");
        var policyArg = new Argument<string>("policyIdOrName", "Policy id (GUID) or name slug");
        var versionArg = new Argument<Guid>("versionId", "Version id (GUID)");
        command.AddArgument(policyArg);
        command.AddArgument(versionArg);
        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var policyIdOrName = ctx.ParseResult.GetValueForArgument(policyArg);
            var versionId = ctx.ParseResult.GetValueForArgument(versionArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var policyId = await ResolvePolicyIdAsync(http, policyIdOrName, ctx, ct).ConfigureAwait(false);
            if (policyId is null)
            {
                return;
            }

            var resp = await http.GetAsync($"/api/policies/{policyId}/versions/{versionId}", ct).ConfigureAwait(false);
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

    private static Command BuildDraftNew(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("draft-new", "Create a new policy with a v1 draft");
        var nameOpt = new Option<string>("--name", "Policy name slug") { IsRequired = true };
        var descOpt = new Option<string?>("--description", "Optional human-readable description");
        var summaryOpt = new Option<string>("--summary", "Version summary") { IsRequired = true };
        var enfOpt = new Option<string>("--enforcement", "may | should | must") { IsRequired = true };
        var sevOpt = new Option<string>("--severity", "info | moderate | critical") { IsRequired = true };
        var scopesOpt = new Option<string[]>("--scopes", "Scope tags (repeat or comma-separate)") { AllowMultipleArgumentsPerToken = true };
        var rulesJsonOpt = new Option<string?>("--rules-json", "Inline rules JSON");
        var rulesFileOpt = new Option<FileInfo?>("--rules-file", "Path to a UTF-8 JSON file containing the rules document");
        command.AddOption(nameOpt);
        command.AddOption(descOpt);
        command.AddOption(summaryOpt);
        command.AddOption(enfOpt);
        command.AddOption(sevOpt);
        command.AddOption(scopesOpt);
        command.AddOption(rulesJsonOpt);
        command.AddOption(rulesFileOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var ct = ctx.GetCancellationToken();

            var rulesJson = await LoadRulesAsync(
                ctx.ParseResult.GetValueForOption(rulesJsonOpt),
                ctx.ParseResult.GetValueForOption(rulesFileOpt),
                ct).ConfigureAwait(false);
            if (rulesJson is null)
            {
                ctx.ExitCode = ExitCodes.BadArguments;
                return;
            }

            var scopes = NormalizeScopes(ctx.ParseResult.GetValueForOption(scopesOpt));
            var payload = new
            {
                name = ctx.ParseResult.GetValueForOption(nameOpt),
                description = ctx.ParseResult.GetValueForOption(descOpt),
                summary = ctx.ParseResult.GetValueForOption(summaryOpt),
                enforcement = ctx.ParseResult.GetValueForOption(enfOpt),
                severity = ctx.ParseResult.GetValueForOption(sevOpt),
                scopes,
                rulesJson,
            };

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.PostAsJsonAsync("/api/policies", payload, ct).ConfigureAwait(false);
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

    private static Command BuildDraftEdit(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("draft-edit", "Edit an existing draft version");
        var policyArg = new Argument<string>("policyIdOrName", "Policy id (GUID) or name slug");
        var versionArg = new Argument<Guid>("versionId", "Draft version id (GUID)");
        var summaryOpt = new Option<string>("--summary", "Version summary") { IsRequired = true };
        var enfOpt = new Option<string>("--enforcement", "may | should | must") { IsRequired = true };
        var sevOpt = new Option<string>("--severity", "info | moderate | critical") { IsRequired = true };
        var scopesOpt = new Option<string[]>("--scopes", "Scope tags (repeat or comma-separate)") { AllowMultipleArgumentsPerToken = true };
        var rulesJsonOpt = new Option<string?>("--rules-json", "Inline rules JSON");
        var rulesFileOpt = new Option<FileInfo?>("--rules-file", "Path to a UTF-8 JSON file containing the rules document");
        command.AddArgument(policyArg);
        command.AddArgument(versionArg);
        command.AddOption(summaryOpt);
        command.AddOption(enfOpt);
        command.AddOption(sevOpt);
        command.AddOption(scopesOpt);
        command.AddOption(rulesJsonOpt);
        command.AddOption(rulesFileOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var policyIdOrName = ctx.ParseResult.GetValueForArgument(policyArg);
            var versionId = ctx.ParseResult.GetValueForArgument(versionArg);
            var ct = ctx.GetCancellationToken();

            var rulesJson = await LoadRulesAsync(
                ctx.ParseResult.GetValueForOption(rulesJsonOpt),
                ctx.ParseResult.GetValueForOption(rulesFileOpt),
                ct).ConfigureAwait(false);
            if (rulesJson is null)
            {
                ctx.ExitCode = ExitCodes.BadArguments;
                return;
            }

            using var http = ClientFactory.Create(api, tok);
            var policyId = await ResolvePolicyIdAsync(http, policyIdOrName, ctx, ct).ConfigureAwait(false);
            if (policyId is null)
            {
                return;
            }

            var scopes = NormalizeScopes(ctx.ParseResult.GetValueForOption(scopesOpt));
            var payload = new
            {
                summary = ctx.ParseResult.GetValueForOption(summaryOpt),
                enforcement = ctx.ParseResult.GetValueForOption(enfOpt),
                severity = ctx.ParseResult.GetValueForOption(sevOpt),
                scopes,
                rulesJson,
            };

            var resp = await http.PutAsJsonAsync($"/api/policies/{policyId}/versions/{versionId}", payload, ct).ConfigureAwait(false);
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

    private static Command BuildDraftBump(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("draft-bump", "Create a new draft version cloned from an existing version");
        var policyArg = new Argument<string>("policyIdOrName", "Policy id (GUID) or name slug");
        var sourceArg = new Argument<Guid>("sourceVersionId", "Source version id (GUID)");
        command.AddArgument(policyArg);
        command.AddArgument(sourceArg);
        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var policyIdOrName = ctx.ParseResult.GetValueForArgument(policyArg);
            var sourceVersionId = ctx.ParseResult.GetValueForArgument(sourceArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var policyId = await ResolvePolicyIdAsync(http, policyIdOrName, ctx, ct).ConfigureAwait(false);
            if (policyId is null)
            {
                return;
            }

            var resp = await http.PostAsync($"/api/policies/{policyId}/versions/{sourceVersionId}/bump", content: null, ct).ConfigureAwait(false);
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

    private static async Task<string?> ResolvePolicyIdAsync(
        HttpClient http,
        string idOrName,
        System.CommandLine.Invocation.InvocationContext ctx,
        CancellationToken ct)
    {
        if (Guid.TryParse(idOrName, out var guid))
        {
            return guid.ToString();
        }
        var resp = await http.GetAsync(PolicyCommands.BuildPolicyRoute(idOrName), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
            return null;
        }
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
        {
            return idProp.GetString();
        }
        await Console.Error.WriteLineAsync("Unexpected policy payload (missing id).").ConfigureAwait(false);
        ctx.ExitCode = ExitCodes.Transport;
        return null;
    }

    private static async Task<string?> LoadRulesAsync(string? inlineJson, FileInfo? file, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(inlineJson) && file is not null)
        {
            await Console.Error.WriteLineAsync("Specify only one of --rules-json or --rules-file.").ConfigureAwait(false);
            return null;
        }
        if (!string.IsNullOrEmpty(inlineJson))
        {
            if (!IsValidJson(inlineJson))
            {
                await Console.Error.WriteLineAsync("--rules-json is not valid JSON.").ConfigureAwait(false);
                return null;
            }
            return inlineJson;
        }
        if (file is not null)
        {
            if (!file.Exists)
            {
                await Console.Error.WriteLineAsync($"--rules-file not found: {file.FullName}").ConfigureAwait(false);
                return null;
            }
            var content = await File.ReadAllTextAsync(file.FullName, ct).ConfigureAwait(false);
            if (!IsValidJson(content))
            {
                await Console.Error.WriteLineAsync($"--rules-file is not valid UTF-8 JSON: {file.FullName}").ConfigureAwait(false);
                return null;
            }
            return content;
        }
        await Console.Error.WriteLineAsync("Provide --rules-json or --rules-file.").ConfigureAwait(false);
        return null;
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> NormalizeScopes(string[]? raw)
    {
        if (raw is null || raw.Length == 0)
        {
            return Array.Empty<string>();
        }
        var result = new List<string>();
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }
            foreach (var part in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                result.Add(part);
            }
        }
        return result;
    }
}
