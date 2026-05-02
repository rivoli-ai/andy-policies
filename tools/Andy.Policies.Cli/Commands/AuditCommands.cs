// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Cli.Http;
using Andy.Policies.Cli.Output;
using Andy.Policies.Shared.Auditing;

namespace Andy.Policies.Cli.Commands;

/// <summary>
/// <c>andy-policies-cli audit verify [--from N] [--to N] [--file path]</c>
/// (P6.5, story rivoli-ai/andy-policies#45). Two modes:
/// <list type="bullet">
///   <item><b>Live</b> — hits <c>GET /api/audit/verify</c> on
///     the configured server. The user's bearer token (via
///     <c>--token</c> or <c>ANDY_CLI_TOKEN</c>) is forwarded.</item>
///   <item><b>Offline</b> — reads an NDJSON export of audit
///     events (one <c>AuditEventDto</c> per line) and re-runs
///     the chain verifier locally using the Shared
///     <c>AuditEnvelopeHasher</c>. Required for external
///     auditors handed an archival copy.</item>
/// </list>
/// Online and offline verification on the same inputs produce
/// identical results — the canonical JSON serialiser and SHA-256
/// helper are the same code on both sides.
/// </summary>
internal static class AuditCommands
{
    private static readonly JsonSerializerOptions NdjsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Register(
        Command parent,
        Option<string> apiUrlOption,
        Option<string?> tokenOption,
        Option<string> outputOption)
    {
        parent.AddCommand(BuildVerify(apiUrlOption, tokenOption, outputOption));
    }

    private static Command BuildVerify(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("verify", "Verify the audit hash chain.");
        var fromOpt = new Option<long?>(
            aliases: new[] { "--from" },
            description: "Inclusive lower seq bound. Defaults to 1.");
        var toOpt = new Option<long?>(
            aliases: new[] { "--to" },
            description: "Inclusive upper seq bound. Defaults to MAX(seq).");
        var fileOpt = new Option<FileInfo?>(
            aliases: new[] { "--file" },
            description: "Path to an NDJSON export (one AuditEventDto per line). " +
                         "When supplied, verification is offline and the API is not contacted.");
        command.AddOption(fromOpt);
        command.AddOption(toOpt);
        command.AddOption(fileOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var from = ctx.ParseResult.GetValueForOption(fromOpt);
            var to = ctx.ParseResult.GetValueForOption(toOpt);
            var file = ctx.ParseResult.GetValueForOption(fileOpt);
            var ct = ctx.GetCancellationToken();

            ctx.ExitCode = file is null
                ? await VerifyLiveAsync(api, tok, fmt, from, to, ct).ConfigureAwait(false)
                : await VerifyOfflineAsync(file, fmt, from, to, ct).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task<int> VerifyLiveAsync(
        string api, string? token, string format, long? from, long? to, CancellationToken ct)
    {
        using var http = ClientFactory.Create(api, token);
        var qs = Querystring.Build(
            ("fromSeq", from?.ToString(CultureInfo.InvariantCulture)),
            ("toSeq", to?.ToString(CultureInfo.InvariantCulture)));
        var resp = await http.GetAsync($"/api/audit/verify{qs}", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
        }
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        OutputRenderer.Write(body, format,
            new[] { "valid", "firstDivergenceSeq", "inspectedCount", "lastSeq" });
        // Exit non-zero on divergence so CI / cron jobs can branch.
        var dto = JsonSerializer.Deserialize<VerifyDto>(body, NdjsonOptions);
        return dto is { Valid: false } ? ExitCodes.AuditDivergence : ExitCodes.Success;
    }

    private static async Task<int> VerifyOfflineAsync(
        FileInfo file, string format, long? from, long? to, CancellationToken ct)
    {
        if (!file.Exists)
        {
            await Console.Error.WriteLineAsync($"File not found: {file.FullName}").ConfigureAwait(false);
            return ExitCodes.BadArguments;
        }

        var rows = new List<AuditEventNdjson>();
        using (var stream = file.OpenRead())
        using (var reader = new StreamReader(stream))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // P6.7 (#48): the export bundle from
                // policy.audit.export carries a "type"
                // discriminator on every line and a trailing
                // summary line. Tolerate both that shape and
                // the plain AuditEventDto-per-line shape so a
                // legacy export still verifies.
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var typeEl)
                    && typeEl.ValueKind == JsonValueKind.String
                    && typeEl.GetString() == "summary")
                {
                    // Summary line: terminal hash sanity-check
                    // happens at the end of the verification
                    // loop; nothing to add to the row list.
                    continue;
                }

                var row = JsonSerializer.Deserialize<AuditEventNdjson>(line, NdjsonOptions)
                    ?? throw new InvalidOperationException(
                        $"Failed to deserialize NDJSON line: {line}");
                rows.Add(row);
            }
        }
        rows.Sort((a, b) => a.Seq.CompareTo(b.Seq));

        var lower = from is { } f && f > 1 ? f : 1;
        var prev = lower > 1
            ? rows.Where(r => r.Seq == lower - 1)
                  .Select(r => Convert.FromHexString(r.PrevHashHex))
                  .FirstOrDefault() ?? new byte[32]
            : new byte[32];
        // Re-seat: when lower>1, prev should be the *hash* of (lower-1), not its prev.
        if (lower > 1)
        {
            var seed = rows.FirstOrDefault(r => r.Seq == lower - 1);
            prev = seed is null ? new byte[32] : Convert.FromHexString(seed.HashHex);
            if (seed is null)
            {
                var unverifiable = new VerifyDto(false, lower, 0, 0);
                Render(unverifiable, format);
                return ExitCodes.AuditDivergence;
            }
        }

        long inspected = 0;
        long lastSeq = lower > 1 ? lower - 1 : 0;
        foreach (var row in rows.Where(r => r.Seq >= lower).Where(r => to is not { } t || r.Seq <= t))
        {
            inspected++;
            lastSeq = row.Seq;

            var rowPrev = Convert.FromHexString(row.PrevHashHex);
            if (!rowPrev.AsSpan().SequenceEqual(prev))
            {
                Render(new VerifyDto(false, row.Seq, inspected, lastSeq), format);
                return ExitCodes.AuditDivergence;
            }

            var recomputed = AuditEnvelopeHasher.ComputeHash(
                prev,
                row.Id,
                row.Timestamp,
                row.ActorSubjectId,
                row.ActorRoles,
                row.Action,
                row.EntityType,
                row.EntityId,
                row.FieldDiffJson,
                row.Rationale);
            var rowHash = Convert.FromHexString(row.HashHex);
            if (!recomputed.AsSpan().SequenceEqual(rowHash))
            {
                Render(new VerifyDto(false, row.Seq, inspected, lastSeq), format);
                return ExitCodes.AuditDivergence;
            }

            prev = rowHash;
        }

        Render(new VerifyDto(true, null, inspected, lastSeq), format);
        return ExitCodes.Success;
    }

    private static void Render(VerifyDto dto, string format)
    {
        var json = JsonSerializer.Serialize(dto, NdjsonOptions);
        OutputRenderer.Write(json, format,
            new[] { "valid", "firstDivergenceSeq", "inspectedCount", "lastSeq" });
    }

    private sealed record VerifyDto(
        bool Valid,
        long? FirstDivergenceSeq,
        long InspectedCount,
        long LastSeq);

    private sealed record AuditEventNdjson(
        Guid Id,
        long Seq,
        string PrevHashHex,
        string HashHex,
        DateTimeOffset Timestamp,
        string ActorSubjectId,
        IReadOnlyList<string> ActorRoles,
        string Action,
        string EntityType,
        string EntityId,
        string FieldDiffJson,
        string? Rationale);
}
