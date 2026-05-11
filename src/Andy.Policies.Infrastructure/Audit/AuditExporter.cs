// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Audit;

/// <summary>
/// EF-backed streaming NDJSON exporter (P6.7, story
/// rivoli-ai/andy-policies#48). Walks <c>audit_events</c> via
/// <see cref="EntityFrameworkQueryableExtensions.AsAsyncEnumerable"/>
/// so peak heap stays bounded regardless of chain size, then
/// emits one JSON object per line followed by a summary line
/// containing the genesis prev-hash, terminal hash, and total
/// count for the exported range.
/// </summary>
public sealed class AuditExporter : IAuditExporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IAuditRetentionPolicy _retention;

    public AuditExporter(AppDbContext db, TimeProvider clock, IAuditRetentionPolicy retention)
    {
        _db = db;
        _clock = clock;
        _retention = retention;
    }

    public async Task WriteNdjsonAsync(
        Stream output, long? fromSeq, long? toSeq, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);

        var writer = new StreamWriter(output, Utf8NoBom, leaveOpen: true);
        try
        {
            var query = _db.AuditEvents.AsNoTracking().AsQueryable();
            if (fromSeq is { } f) query = query.Where(e => e.Seq >= f);
            if (toSeq is { } t) query = query.Where(e => e.Seq <= t);
            query = query.OrderBy(e => e.Seq);

            // ADR 0006.1: events with Timestamp < staleThreshold get
            // a "stale": true marker on their NDJSON line. When
            // retention is disabled (setting = 0), threshold is null
            // and no event is flagged.
            var staleThreshold = _retention.GetStalenessThreshold(_clock.GetUtcNow());

            long count = 0;
            long firstSeq = 0;
            long lastSeq = 0;
            string? genesisPrevHex = null;
            string? terminalHashHex = null;

            await foreach (var ev in query.AsAsyncEnumerable().WithCancellation(ct))
            {
                count++;
                if (genesisPrevHex is null)
                {
                    firstSeq = ev.Seq;
                    genesisPrevHex = Convert.ToHexString(ev.PrevHash).ToLowerInvariant();
                }
                lastSeq = ev.Seq;
                terminalHashHex = Convert.ToHexString(ev.Hash).ToLowerInvariant();

                var stale = staleThreshold is { } threshold && ev.Timestamp < threshold;
                await writer.WriteLineAsync(SerializeEventLine(ev, stale)).ConfigureAwait(false);
            }

            // Always emit a summary line, even on an empty range —
            // the auditor gets a stable bundle shape regardless.
            await writer.WriteLineAsync(SerializeSummaryLine(
                fromSeq: count > 0 ? firstSeq : (fromSeq ?? 0),
                toSeq: count > 0 ? lastSeq : (toSeq ?? 0),
                count: count,
                genesisPrevHex: genesisPrevHex ?? new string('0', 64),
                terminalHashHex: terminalHashHex,
                exportedAt: _clock.GetUtcNow()))
                .ConfigureAwait(false);
        }
        finally
        {
            await writer.FlushAsync(ct).ConfigureAwait(false);
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string SerializeEventLine(AuditEvent ev, bool stale)
    {
        // Embed the patch document as parsed JSON, not as a
        // string, so the bundle line-by-line is interchangeable
        // with the AuditEventDto wire format the verifier
        // already understands.
        using var diffDoc = JsonDocument.Parse(
            string.IsNullOrEmpty(ev.FieldDiffJson) ? "[]" : ev.FieldDiffJson);

        // Only emit the stale field when true. ADR 0006.1 §1 keeps
        // the marker advisory; absence reads as "fresh" without
        // adding a boolean column to every non-retention export.
        if (stale)
        {
            var staleLine = new
            {
                type = "event",
                id = ev.Id,
                seq = ev.Seq,
                prevHashHex = Convert.ToHexString(ev.PrevHash).ToLowerInvariant(),
                hashHex = Convert.ToHexString(ev.Hash).ToLowerInvariant(),
                timestamp = ev.Timestamp,
                actorSubjectId = ev.ActorSubjectId,
                actorRoles = ev.ActorRoles,
                action = ev.Action,
                entityType = ev.EntityType,
                entityId = ev.EntityId,
                fieldDiff = diffDoc.RootElement,
                rationale = ev.Rationale,
                stale = true,
            };
            return JsonSerializer.Serialize(staleLine, WireOptions);
        }

        var line = new
        {
            type = "event",
            id = ev.Id,
            seq = ev.Seq,
            prevHashHex = Convert.ToHexString(ev.PrevHash).ToLowerInvariant(),
            hashHex = Convert.ToHexString(ev.Hash).ToLowerInvariant(),
            timestamp = ev.Timestamp,
            actorSubjectId = ev.ActorSubjectId,
            actorRoles = ev.ActorRoles,
            action = ev.Action,
            entityType = ev.EntityType,
            entityId = ev.EntityId,
            fieldDiff = diffDoc.RootElement,
            rationale = ev.Rationale,
        };
        return JsonSerializer.Serialize(line, WireOptions);
    }

    private static string SerializeSummaryLine(
        long fromSeq, long toSeq, long count,
        string genesisPrevHex, string? terminalHashHex,
        DateTimeOffset exportedAt)
    {
        var summary = new
        {
            type = "summary",
            fromSeq,
            toSeq,
            count,
            genesisPrevHashHex = genesisPrevHex,
            terminalHashHex = terminalHashHex ?? new string('0', 64),
            exportedAt,
        };
        return JsonSerializer.Serialize(summary, WireOptions);
    }
}
