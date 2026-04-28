// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.Net.Http.Json;
using Andy.Policies.Cli.Http;
using Andy.Policies.Cli.Output;

namespace Andy.Policies.Cli.Commands;

/// <summary>
/// <c>andy-policies-cli bindings {list,create,delete,resolve}</c>
/// (P3.7, story rivoli-ai/andy-policies#25). Mirrors the binding REST
/// surface from P3.3/P3.4: <c>list</c> takes either
/// <c>--policy-version-id</c> or (<c>--target-type</c> +
/// <c>--target-ref</c>) and exits with a usage error if neither is
/// supplied; <c>create</c> POSTs to <c>/api/bindings</c>;
/// <c>delete</c> issues a DELETE with optional <c>--rationale</c>
/// query string; <c>resolve</c> hits the joined-read endpoint.
/// </summary>
internal static class BindingCommands
{
    public static void Register(
        Command parent,
        Option<string> apiUrlOption,
        Option<string?> tokenOption,
        Option<string> outputOption)
    {
        parent.AddCommand(BuildList(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildCreate(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildDelete(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildResolve(apiUrlOption, tokenOption, outputOption));
    }

    private static Command BuildList(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("list", "List bindings for a policy version or target.");
        var policyVersionOpt = new Option<Guid?>(
            aliases: new[] { "--policy-version-id" },
            description: "Filter by policy version id (GUID). Mutually exclusive with --target-type/--target-ref.");
        var targetTypeOpt = new Option<string?>(
            aliases: new[] { "--target-type" },
            description: "Filter by target type. One of: Template, Repo, ScopeNode, Tenant, Org.");
        var targetRefOpt = new Option<string?>(
            aliases: new[] { "--target-ref" },
            description: "Filter by target reference (e.g. 'template:abc', 'repo:org/name').");
        var includeDeletedOpt = new Option<bool>(
            aliases: new[] { "--include-deleted" },
            getDefaultValue: () => false,
            description: "Include soft-deleted (tombstoned) bindings (only with --policy-version-id).");
        command.AddOption(policyVersionOpt);
        command.AddOption(targetTypeOpt);
        command.AddOption(targetRefOpt);
        command.AddOption(includeDeletedOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var pv = ctx.ParseResult.GetValueForOption(policyVersionOpt);
            var tt = ctx.ParseResult.GetValueForOption(targetTypeOpt);
            var tr = ctx.ParseResult.GetValueForOption(targetRefOpt);
            var includeDeleted = ctx.ParseResult.GetValueForOption(includeDeletedOpt);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            HttpResponseMessage resp;
            if (pv is not null)
            {
                // Version-rooted enumeration. Caller can ask for tombstones too.
                var url = $"/api/policies/00000000-0000-0000-0000-000000000000/versions/{pv}/bindings?includeDeleted={includeDeleted.ToString().ToLowerInvariant()}";
                resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(tt) && !string.IsNullOrEmpty(tr))
            {
                resp = await http.GetAsync(
                    $"/api/bindings?targetType={Uri.EscapeDataString(tt)}&targetRef={Uri.EscapeDataString(tr)}",
                    ct).ConfigureAwait(false);
            }
            else
            {
                await Console.Error.WriteLineAsync(
                    "Either --policy-version-id or both --target-type and --target-ref are required.")
                    .ConfigureAwait(false);
                ctx.ExitCode = ExitCodes.BadArguments;
                return;
            }

            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OutputRenderer.Write(body, fmt, new[] { "id", "policyVersionId", "targetType", "targetRef", "bindStrength" });
        });
        return command;
    }

    private static Command BuildCreate(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("create", "Create a binding linking a policy version to a target.");
        var policyVersionOpt = new Option<Guid>(
            aliases: new[] { "--policy-version-id" },
            description: "Target policy version id (GUID).") { IsRequired = true };
        var targetTypeOpt = new Option<string>(
            aliases: new[] { "--target-type" },
            description: "One of: Template, Repo, ScopeNode, Tenant, Org.") { IsRequired = true };
        var targetRefOpt = new Option<string>(
            aliases: new[] { "--target-ref" },
            description: "Target reference (e.g. 'template:abc', 'repo:org/name').") { IsRequired = true };
        var bindStrengthOpt = new Option<string>(
            aliases: new[] { "--bind-strength" },
            getDefaultValue: () => "Recommended",
            description: "Mandatory or Recommended. Defaults to Recommended.");
        command.AddOption(policyVersionOpt);
        command.AddOption(targetTypeOpt);
        command.AddOption(targetRefOpt);
        command.AddOption(bindStrengthOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var pv = ctx.ParseResult.GetValueForOption(policyVersionOpt);
            var tt = ctx.ParseResult.GetValueForOption(targetTypeOpt)!;
            var tr = ctx.ParseResult.GetValueForOption(targetRefOpt)!;
            var bs = ctx.ParseResult.GetValueForOption(bindStrengthOpt)!;
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.PostAsJsonAsync(
                "/api/bindings",
                new { policyVersionId = pv, targetType = tt, targetRef = tr, bindStrength = bs },
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

    private static Command BuildDelete(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("delete", "Soft-delete a binding (DeletedAt stamped; row preserved for audit).");
        var idArg = new Argument<Guid>("bindingId", "Binding id (GUID)");
        var rationaleOpt = new Option<string?>(
            aliases: new[] { "--rationale", "-r" },
            description: "Rationale recorded against the audit chain.");
        command.AddArgument(idArg);
        command.AddOption(rationaleOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var rationale = ctx.ParseResult.GetValueForOption(rationaleOpt);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var url = string.IsNullOrEmpty(rationale)
                ? $"/api/bindings/{id}"
                : $"/api/bindings/{id}?rationale={Uri.EscapeDataString(rationale)}";
            var resp = await http.DeleteAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            // 204 No Content; nothing to render.
        });
        return command;
    }

    private static Command BuildResolve(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("resolve", "Resolve all live bindings for an exact target (joins policy + version).");
        var targetTypeOpt = new Option<string>(
            aliases: new[] { "--target-type" },
            description: "One of: Template, Repo, ScopeNode, Tenant, Org.") { IsRequired = true };
        var targetRefOpt = new Option<string>(
            aliases: new[] { "--target-ref" },
            description: "Target reference (exact match, no case-folding).") { IsRequired = true };
        command.AddOption(targetTypeOpt);
        command.AddOption(targetRefOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var tt = ctx.ParseResult.GetValueForOption(targetTypeOpt)!;
            var tr = ctx.ParseResult.GetValueForOption(targetRefOpt)!;
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync(
                $"/api/bindings/resolve?targetType={Uri.EscapeDataString(tt)}&targetRef={Uri.EscapeDataString(tr)}",
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
}
