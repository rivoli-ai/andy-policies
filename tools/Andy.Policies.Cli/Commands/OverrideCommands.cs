// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Andy.Policies.Cli.Http;
using Andy.Policies.Cli.Output;

namespace Andy.Policies.Cli.Commands;

/// <summary>
/// <c>andy-policies-cli overrides {propose,approve,revoke,list,get,active}</c>
/// (P5.7, story rivoli-ai/andy-policies#60). Mirrors the REST surface
/// from P5.5: writes go to <c>POST /api/overrides[/approve|/revoke]</c>;
/// reads to <c>GET /api/overrides[/{id}|/active]</c>. Output flows through
/// the shared <see cref="OutputRenderer"/> so <c>--output table|json|yaml</c>
/// behaves the same as the other resource commands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Settings gate inheritance:</b> the CLI is a thin REST client.
/// When <c>andy.policies.experimentalOverridesEnabled</c> is off, the
/// API returns HTTP 403 with <c>errorCode = "override.disabled"</c>;
/// <see cref="ExitCodes.HandleAsync"/> turns that into a non-zero exit
/// code and prints the ProblemDetails to stderr. No CLI-side gate
/// duplication needed — see #56 §"Surface enforcement".
/// </para>
/// <para>
/// <b>Expires-at parsing:</b> accepts ISO 8601 (<c>2026-05-01T00:00:00Z</c>)
/// or relative durations (<c>+30d</c>, <c>+8h</c>, <c>+45m</c>). Past
/// values fail-fast at the CLI layer with a clear stderr message —
/// the API's <c>+1m</c> floor is the server-side belt; this is the
/// braces.
/// </para>
/// </remarks>
internal static class OverrideCommands
{
    public static void Register(
        Command parent,
        Option<string> apiUrlOption,
        Option<string?> tokenOption,
        Option<string> outputOption)
    {
        parent.AddCommand(BuildPropose(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildApprove(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildRevoke(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildList(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildGet(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildActive(apiUrlOption, tokenOption, outputOption));
    }

    /// <summary>
    /// Parse <c>--expires-at</c>: accepts ISO 8601 or relative
    /// durations <c>+Nd</c> / <c>+Nh</c> / <c>+Nm</c>. Returns null on
    /// failure (the caller is expected to print a message + set the
    /// exit code).
    /// </summary>
    internal static DateTimeOffset? ParseExpiresAt(string raw, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var match = Regex.Match(raw.Trim(), @"^\+(\d+)([dhm])$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                || n <= 0)
            {
                return null;
            }
            return char.ToLowerInvariant(match.Groups[2].Value[0]) switch
            {
                'd' => now.AddDays(n),
                'h' => now.AddHours(n),
                'm' => now.AddMinutes(n),
                _ => null,
            };
        }

        return DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var iso)
            ? iso
            : null;
    }

    private static Command BuildPropose(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("propose", "Propose a new policy override.");
        var policyVersionOpt = new Option<Guid>(
            aliases: new[] { "--policy-version-id" },
            description: "Target policy version id (GUID).") { IsRequired = true };
        var scopeKindOpt = new Option<string>(
            aliases: new[] { "--scope-kind" },
            description: "Principal or Cohort.") { IsRequired = true };
        scopeKindOpt.FromAmong("Principal", "Cohort");
        var scopeRefOpt = new Option<string>(
            aliases: new[] { "--scope-ref" },
            description: "Opaque scope ref (e.g. 'user:42', 'cohort:beta'); ≤256 chars.") { IsRequired = true };
        var effectOpt = new Option<string>(
            aliases: new[] { "--effect" },
            description: "Exempt or Replace.") { IsRequired = true };
        effectOpt.FromAmong("Exempt", "Replace");
        var replacementOpt = new Option<Guid?>(
            aliases: new[] { "--replacement-policy-version-id" },
            description: "Required when --effect=Replace; otherwise omit.");
        var expiresAtOpt = new Option<string>(
            aliases: new[] { "--expires-at" },
            description: "ISO 8601 timestamp (e.g. 2026-05-01T00:00:00Z) or relative duration (+30d, +8h, +45m). Must be ≥1 minute in the future.") { IsRequired = true };
        var rationaleOpt = new Option<string>(
            aliases: new[] { "--rationale", "-r" },
            description: "Required non-empty rationale; ≤2000 chars.") { IsRequired = true };
        command.AddOption(policyVersionOpt);
        command.AddOption(scopeKindOpt);
        command.AddOption(scopeRefOpt);
        command.AddOption(effectOpt);
        command.AddOption(replacementOpt);
        command.AddOption(expiresAtOpt);
        command.AddOption(rationaleOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var pv = ctx.ParseResult.GetValueForOption(policyVersionOpt);
            var sk = ctx.ParseResult.GetValueForOption(scopeKindOpt)!;
            var sr = ctx.ParseResult.GetValueForOption(scopeRefOpt)!;
            var ef = ctx.ParseResult.GetValueForOption(effectOpt)!;
            var repl = ctx.ParseResult.GetValueForOption(replacementOpt);
            var exp = ctx.ParseResult.GetValueForOption(expiresAtOpt)!;
            var rationale = ctx.ParseResult.GetValueForOption(rationaleOpt)!;
            var ct = ctx.GetCancellationToken();

            var now = DateTimeOffset.UtcNow;
            var parsedExpiry = ParseExpiresAt(exp, now);
            if (parsedExpiry is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Invalid --expires-at value '{exp}'. Use ISO 8601 (2026-05-01T00:00:00Z) or +Nd/+Nh/+Nm.")
                    .ConfigureAwait(false);
                ctx.ExitCode = ExitCodes.BadArguments;
                return;
            }
            if (parsedExpiry.Value <= now.AddMinutes(1))
            {
                await Console.Error.WriteLineAsync(
                    $"--expires-at must be at least 1 minute in the future (resolved to {parsedExpiry.Value:o}).")
                    .ConfigureAwait(false);
                ctx.ExitCode = ExitCodes.BadArguments;
                return;
            }

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.PostAsJsonAsync(
                "/api/overrides",
                new
                {
                    policyVersionId = pv,
                    scopeKind = sk,
                    scopeRef = sr,
                    effect = ef,
                    replacementPolicyVersionId = repl,
                    expiresAt = parsedExpiry.Value,
                    rationale,
                },
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

    private static Command BuildApprove(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("approve", "Approve a proposed override (must differ from proposer).");
        var idArg = new Argument<Guid>("id", "Override id (GUID)");
        command.AddArgument(idArg);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.PostAsync(
                $"/api/overrides/{id}/approve", content: null, ct).ConfigureAwait(false);
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

    private static Command BuildRevoke(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("revoke", "Revoke a proposed or approved override.");
        var idArg = new Argument<Guid>("id", "Override id (GUID)");
        var reasonOpt = new Option<string>(
            aliases: new[] { "--reason", "-r" },
            description: "Required non-empty revocation reason; ≤2000 chars.") { IsRequired = true };
        command.AddArgument(idArg);
        command.AddOption(reasonOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var reason = ctx.ParseResult.GetValueForOption(reasonOpt)!;
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.PostAsJsonAsync(
                $"/api/overrides/{id}/revoke",
                new { revocationReason = reason },
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
        var command = new Command("list", "List overrides with optional filters.");
        var stateOpt = new Option<string?>(
            aliases: new[] { "--state" },
            description: "Filter by state. One of: Proposed, Approved, Revoked, Expired.");
        var scopeKindOpt = new Option<string?>(
            aliases: new[] { "--scope-kind" },
            description: "Filter by scope kind: Principal or Cohort.");
        var scopeRefOpt = new Option<string?>(
            aliases: new[] { "--scope-ref" },
            description: "Filter by exact scope ref.");
        var policyVersionOpt = new Option<Guid?>(
            aliases: new[] { "--policy-version-id" },
            description: "Filter by policy version id (GUID).");
        command.AddOption(stateOpt);
        command.AddOption(scopeKindOpt);
        command.AddOption(scopeRefOpt);
        command.AddOption(policyVersionOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var state = ctx.ParseResult.GetValueForOption(stateOpt);
            var sk = ctx.ParseResult.GetValueForOption(scopeKindOpt);
            var sr = ctx.ParseResult.GetValueForOption(scopeRefOpt);
            var pv = ctx.ParseResult.GetValueForOption(policyVersionOpt);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var qs = Querystring.Build(
                ("state", state),
                ("scopeKind", sk),
                ("scopeRef", sr),
                ("policyVersionId", pv?.ToString()));
            var url = $"/api/overrides{qs}";

            var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OutputRenderer.Write(body, fmt,
                new[] { "id", "state", "scopeKind", "scopeRef", "effect", "expiresAt" });
        });
        return command;
    }

    private static Command BuildGet(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("get", "Fetch a single override by id.");
        var idArg = new Argument<Guid>("id", "Override id (GUID)");
        command.AddArgument(idArg);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync($"/api/overrides/{id}", ct).ConfigureAwait(false);
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

    private static Command BuildActive(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("active",
            "Currently-effective overrides for a scope (Approved + non-expired).");
        var scopeKindOpt = new Option<string>(
            aliases: new[] { "--scope-kind" },
            description: "Principal or Cohort.") { IsRequired = true };
        scopeKindOpt.FromAmong("Principal", "Cohort");
        var scopeRefOpt = new Option<string>(
            aliases: new[] { "--scope-ref" },
            description: "Exact scope ref.") { IsRequired = true };
        command.AddOption(scopeKindOpt);
        command.AddOption(scopeRefOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var sk = ctx.ParseResult.GetValueForOption(scopeKindOpt)!;
            var sr = ctx.ParseResult.GetValueForOption(scopeRefOpt)!;
            var ct = ctx.GetCancellationToken();

            using var http = ClientFactory.Create(api, tok);
            var qs = Querystring.Build(("scopeKind", sk), ("scopeRef", sr));
            var resp = await http.GetAsync($"/api/overrides/active{qs}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OutputRenderer.Write(body, fmt,
                new[] { "id", "policyVersionId", "scopeRef", "effect", "approvedAt", "expiresAt" });
        });
        return command;
    }
}
