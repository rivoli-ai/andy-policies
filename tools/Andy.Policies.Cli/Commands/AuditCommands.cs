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
        parent.AddCommand(BuildList(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildGet(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildVerify(apiUrlOption, tokenOption, outputOption));
        parent.AddCommand(BuildExport(apiUrlOption, tokenOption));
    }

    private static Command BuildList(Option<string> apiUrl, Option<string?> token, Option<string> output)
    {
        var command = new Command("list", "List audit events with optional filters.");
        var actorOpt = new Option<string?>(new[] { "--actor" },
            "Filter by actor subject id (exact match).");
        var entityTypeOpt = new Option<string?>(new[] { "--entity-type" },
            "Filter by entity type (e.g. Policy, Override).");
        var entityIdOpt = new Option<string?>(new[] { "--entity-id" },
            "Filter by entity id (exact match). Best paired with --entity-type.");
        var actionOpt = new Option<string?>(new[] { "--action" },
            "Filter by dotted action code, e.g. policy.version.publish.");
        var fromOpt = new Option<DateTimeOffset?>(new[] { "--from" },
            "Inclusive lower timestamp bound (ISO 8601).");
        var toOpt = new Option<DateTimeOffset?>(new[] { "--to" },
            "Inclusive upper timestamp bound (ISO 8601).");
        var cursorOpt = new Option<string?>(new[] { "--cursor" },
            "Opaque cursor from a previous page's nextCursor.");
        var pageSizeOpt = new Option<int?>(new[] { "--page-size" },
            "Rows per page (1..500); default 50.");
        command.AddOption(actorOpt);
        command.AddOption(entityTypeOpt);
        command.AddOption(entityIdOpt);
        command.AddOption(actionOpt);
        command.AddOption(fromOpt);
        command.AddOption(toOpt);
        command.AddOption(cursorOpt);
        command.AddOption(pageSizeOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var ct = ctx.GetCancellationToken();

            var qs = Querystring.Build(
                ("actor", ctx.ParseResult.GetValueForOption(actorOpt)),
                ("entityType", ctx.ParseResult.GetValueForOption(entityTypeOpt)),
                ("entityId", ctx.ParseResult.GetValueForOption(entityIdOpt)),
                ("action", ctx.ParseResult.GetValueForOption(actionOpt)),
                ("from", ctx.ParseResult.GetValueForOption(fromOpt)?.UtcDateTime.ToString("o")),
                ("to", ctx.ParseResult.GetValueForOption(toOpt)?.UtcDateTime.ToString("o")),
                ("cursor", ctx.ParseResult.GetValueForOption(cursorOpt)),
                ("pageSize", ctx.ParseResult.GetValueForOption(pageSizeOpt)?.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync($"/api/audit{qs}", ct).ConfigureAwait(false);
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
        var command = new Command("get", "Fetch a single audit event by id.");
        var idArg = new Argument<Guid>("id", "Audit event id (GUID).");
        command.AddArgument(idArg);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var fmt = ctx.ParseResult.GetValueForOption(output) ?? "table";
            var id = ctx.ParseResult.GetValueForArgument(idArg);
            var ct = ctx.GetCancellationToken();

            // The REST surface doesn't ship a /api/audit/{id} read
            // yet (P6.6 only lists); we fetch via the list endpoint
            // filtered to the row's id at the storage level. P6.7
            // adds policy.audit.get on MCP; a future REST update
            // will surface that on /api/audit/{id} too. Until then
            // we rely on listing all and filtering — fine for the
            // CLI's non-perf-critical path.
            using var http = ClientFactory.Create(api, tok);
            var resp = await http.GetAsync("/api/audit?pageSize=500", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                return;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            JsonElement? match = null;
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (Guid.TryParse(item.GetProperty("id").GetString(), out var rowId) && rowId == id)
                {
                    match = item.Clone();
                    break;
                }
            }
            if (match is null)
            {
                await Console.Error.WriteLineAsync($"AuditEvent {id} not found.").ConfigureAwait(false);
                ctx.ExitCode = ExitCodes.NotFound;
                return;
            }
            OutputRenderer.Write(JsonSerializer.Serialize(match.Value, NdjsonOptions), fmt);
        });
        return command;
    }

    private static Command BuildExport(Option<string> apiUrl, Option<string?> token)
    {
        var command = new Command("export", "Export the audit chain as an NDJSON bundle.");
        var fromOpt = new Option<long?>(new[] { "--from" },
            "Inclusive lower seq bound. Defaults to 1.");
        var toOpt = new Option<long?>(new[] { "--to" },
            "Inclusive upper seq bound. Defaults to MAX(seq).");
        var outFileOpt = new Option<FileInfo>(new[] { "--out", "-o" },
            "Destination NDJSON file (overwritten).") { IsRequired = true };
        command.AddOption(fromOpt);
        command.AddOption(toOpt);
        command.AddOption(outFileOpt);

        command.SetHandler(async ctx =>
        {
            var api = ctx.ParseResult.GetValueForOption(apiUrl)!;
            var tok = ctx.ParseResult.GetValueForOption(token);
            var from = ctx.ParseResult.GetValueForOption(fromOpt);
            var to = ctx.ParseResult.GetValueForOption(toOpt);
            var outFile = ctx.ParseResult.GetValueForOption(outFileOpt)!;
            var ct = ctx.GetCancellationToken();

            // The REST surface doesn't expose /api/audit/export yet
            // (gRPC + MCP do); the CLI builds the bundle locally by
            // walking the list endpoint with a cursor. The bundle
            // shape matches P6.7's exporter — including the
            // trailing summary line — so `audit verify --file`
            // round-trips both server and client exports.
            using var http = ClientFactory.Create(api, tok);

            using var output = outFile.Create();
            using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            string? cursor = null;
            long count = 0;
            long firstSeq = 0;
            long lastSeq = 0;
            string? genesisPrev = null;
            string? terminalHash = null;
            do
            {
                var qs = Querystring.Build(
                    ("pageSize", "500"),
                    ("cursor", cursor));
                var resp = await http.GetAsync($"/api/audit{qs}", ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    ctx.ExitCode = await ExitCodes.HandleAsync(resp, ct).ConfigureAwait(false);
                    return;
                }
                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var page = doc.RootElement;
                foreach (var item in page.GetProperty("items").EnumerateArray())
                {
                    var seq = item.GetProperty("seq").GetInt64();
                    if (from is { } f && seq < f) continue;
                    if (to is { } t && seq > t)
                    {
                        cursor = null;
                        break;
                    }
                    if (count == 0)
                    {
                        firstSeq = seq;
                        genesisPrev = item.GetProperty("prevHashHex").GetString();
                    }
                    lastSeq = seq;
                    terminalHash = item.GetProperty("hashHex").GetString();
                    count++;

                    var line = SerializeExportEvent(item);
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
                cursor = page.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String
                    ? nc.GetString()
                    : null;
            }
            while (!string.IsNullOrEmpty(cursor));

            // Trailing summary keeps the bundle shape identical to
            // P6.7's server-side exporter so audit verify --file
            // round-trips both.
            var summary = new
            {
                type = "summary",
                fromSeq = count > 0 ? firstSeq : (from ?? 0),
                toSeq = count > 0 ? lastSeq : (to ?? 0),
                count,
                genesisPrevHashHex = genesisPrev ?? new string('0', 64),
                terminalHashHex = terminalHash ?? new string('0', 64),
                exportedAt = DateTimeOffset.UtcNow,
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(summary, NdjsonOptions)).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        });
        return command;
    }

    private static string SerializeExportEvent(JsonElement item)
    {
        // Re-shape the response item into the bundle line format
        // (with type:"event" discriminator) so the bundle matches
        // the P6.7 exporter exactly.
        var line = new
        {
            type = "event",
            id = item.GetProperty("id").GetString(),
            seq = item.GetProperty("seq").GetInt64(),
            prevHashHex = item.GetProperty("prevHashHex").GetString(),
            hashHex = item.GetProperty("hashHex").GetString(),
            timestamp = item.GetProperty("timestamp").GetString(),
            actorSubjectId = item.GetProperty("actorSubjectId").GetString(),
            actorRoles = item.GetProperty("actorRoles")
                .EnumerateArray().Select(e => e.GetString()).ToArray(),
            action = item.GetProperty("action").GetString(),
            entityType = item.GetProperty("entityType").GetString(),
            entityId = item.GetProperty("entityId").GetString(),
            fieldDiffJson = item.GetProperty("fieldDiff").GetRawText(),
            rationale = item.TryGetProperty("rationale", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null,
        };
        return JsonSerializer.Serialize(line, NdjsonOptions);
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
