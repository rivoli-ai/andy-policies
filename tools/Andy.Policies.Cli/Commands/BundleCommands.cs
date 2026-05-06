// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.Net.Http.Json;
using Andy.Policies.Cli.Http;
using Andy.Policies.Cli.Output;

namespace Andy.Policies.Cli.Commands;

/// <summary>
/// <c>andy-policies-cli bundles {create,list,get,resolve,diff}</c>
/// (P8.6, story rivoli-ai/andy-policies#86). Mirrors the REST
/// surface from P8.3 + the new <c>GET /api/bundles/{id}/diff</c>
/// endpoint introduced in this story. Output flows through the
/// shared <see cref="OutputRenderer"/> so <c>--output table|json|yaml</c>
/// behaves the same as the other resource commands.
/// </summary>
/// <remarks>
/// <para>
/// <b>HTTP-only.</b> The shared CLI stack speaks REST throughout —
/// no <c>--grpc</c> transport switch. The gRPC <see cref="Andy.Policies.Api.GrpcServices.BundleGrpcService"/>
/// exists for non-CLI consumers and is exercised via integration
/// tests; the CLI keeps a single transport so error-handling +
/// output-rendering paths stay uniform across commands.
/// </para>
/// <para>
/// <b>Diff format.</b> <c>bundles diff</c> emits the RFC-6902 patch
/// JSON verbatim by default — that's the canonical machine-readable
/// shape consumers pipe through patch appliers. <c>--output table</c>
/// renders one row per op for human eyeballing.
/// </para>
/// </remarks>
internal static class BundleCommands
{
    public static void Register(
        Command parent,
        Option<string> apiUrlOption,
        Option<string?> tokenOption,
        Option<string> outputOption)
    {
        parent.AddCommand(BuildCreate(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildList(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildGet(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildResolve(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildDiff(apiUrlOption, tokenOption, outputOption));
    }

    private static Command BuildCreate(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("create", "Create a new bundle (frozen snapshot of the live catalog).");
        var nameOpt = new Option<string>(
            aliases: new[] { "--name", "-n" },
            description: "Slug, ^[a-z0-9][a-z0-9-]{0,62}$.") { IsRequired = true };
        var rationaleOpt = new Option<string>(
            aliases: new[] { "--rationale", "-r" },
            description: "Required non-empty rationale captured in the audit chain.") { IsRequired = true };
        var descriptionOpt = new Option<string?>(
            aliases: new[] { "--description", "-d" },
            description: "Optional human-readable description.");
        command.AddOption(nameOpt);
        command.AddOption(rationaleOpt);
        command.AddOption(descriptionOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var name = ctx.ParseResult.GetValueForOption(nameOpt)!;
            var rationale = ctx.ParseResult.GetValueForOption(rationaleOpt)!;
            var description = ctx.ParseResult.GetValueForOption(descriptionOpt);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.PostAsJsonAsync(
                "/api/bundles",
                new { name, description, rationale },
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

    private static Command BuildList(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("list", "List bundles (active by default).");
        var includeDeletedOpt = new Option<bool>(
            aliases: new[] { "--include-deleted" },
            description: "Include soft-deleted bundles (default false).");
        var skipOpt = new Option<int>(aliases: new[] { "--skip" }, description: "Pagination skip (default 0).");
        var takeOpt = new Option<int>(
            aliases: new[] { "--take" },
            description: "Pagination take; clamped to [1, 200] (default 50).");
        command.AddOption(includeDeletedOpt);
        command.AddOption(skipOpt);
        command.AddOption(takeOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var includeDeleted = ctx.ParseResult.GetValueForOption(includeDeletedOpt);
            var skip = ctx.ParseResult.GetValueForOption(skipOpt);
            var take = ctx.ParseResult.GetValueForOption(takeOpt);
            var ct = ctx.GetCancellationToken();

            // The REST surface (P8.3) doesn't currently expose a list
            // endpoint — that lands as part of this story's CLI work
            // implicitly via /api/bundles?... — but for now, the CLI
            // hits the existing gRPC-equivalent through REST-style
            // query params on /api/bundles. If the REST list endpoint
            // is missing, surface a clear NotImplemented.
            var query = Querystring.Build(
                ("includeDeleted", includeDeleted ? "true" : null),
                ("skip", skip > 0 ? skip.ToString() : null),
                ("take", take > 0 ? take.ToString() : null));

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync($"/api/bundles{query}", ct).ConfigureAwait(false);
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

    private static Command BuildGet(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("get", "Get a bundle by id.");
        var idArg = new Argument<Guid>("id", "Bundle id (GUID).");
        command.AddArgument(idArg);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync($"/api/bundles/{id}", ct).ConfigureAwait(false);
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

    private static Command BuildResolve(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("resolve", "Resolve bindings for a target against a frozen bundle snapshot.");
        var idArg = new Argument<Guid>("id", "Bundle id (GUID).");
        var targetTypeOpt = new Option<string>(
            aliases: new[] { "--target-type" },
            description: "One of Template, Repo, ScopeNode, Tenant, Org.") { IsRequired = true };
        var targetRefOpt = new Option<string>(
            aliases: new[] { "--target-ref" },
            description: "Target reference, e.g. 'repo:rivoli-ai/conductor'.") { IsRequired = true };
        command.AddArgument(idArg);
        command.AddOption(targetTypeOpt);
        command.AddOption(targetRefOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var targetType = ctx.ParseResult.GetValueForOption(targetTypeOpt)!;
            var targetRef = ctx.ParseResult.GetValueForOption(targetRefOpt)!;
            var ct = ctx.GetCancellationToken();

            var query = Querystring.Build(
                ("targetType", targetType),
                ("targetRef", targetRef));

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync($"/api/bundles/{id}/resolve{query}", ct).ConfigureAwait(false);
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

    private static Command BuildDiff(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command(
            "diff",
            "Emit an RFC-6902 JSON Patch between two bundles' frozen snapshots.");
        var fromOpt = new Option<Guid>(
            aliases: new[] { "--from" },
            description: "Source bundle id (GUID).") { IsRequired = true };
        var toOpt = new Option<Guid>(
            aliases: new[] { "--to" },
            description: "Target bundle id (GUID).") { IsRequired = true };
        command.AddOption(fromOpt);
        command.AddOption(toOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var from = ctx.ParseResult.GetValueForOption(fromOpt);
            var to = ctx.ParseResult.GetValueForOption(toOpt);
            var ct = ctx.GetCancellationToken();

            var query = Querystring.Build(("to", to.ToString()));

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync($"/api/bundles/{from}/diff{query}", ct).ConfigureAwait(false);
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
