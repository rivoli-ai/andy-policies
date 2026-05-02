// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Audit;

/// <summary>
/// EF-backed cursor-paginated query over <c>audit_events</c>
/// (P6.6, story rivoli-ai/andy-policies#46). Reads with
/// <c>AsNoTracking</c> — the table is append-only, so the
/// change tracker can only get in the way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Indexes used.</b> The composite filter
/// <c>(EntityType, EntityId)</c> is covered by
/// <c>ix_audit_events_entity</c> from P6.1; <c>actor</c> by
/// <c>ix_audit_events_actor</c>; <c>timestamp</c> by
/// <c>ix_audit_events_timestamp</c>. The cursor
/// <c>WHERE seq &gt; @after</c> is the unique index
/// <c>ix_audit_events_seq</c>.
/// </para>
/// <para>
/// <b>Peek-plus-one trick.</b> The query <c>.Take(pageSize + 1)</c>
/// then trims to <paramref name="filter"/>.PageSize rows in
/// memory; if exactly <c>pageSize + 1</c> came back, the
/// page-after-last is non-empty and we emit a
/// <c>NextCursor</c>. Saves a separate <c>COUNT(*)</c> round-
/// trip.
/// </para>
/// </remarks>
public sealed class AuditQuery : IAuditQuery
{
    private readonly AppDbContext _db;

    public AuditQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AuditPageDto> QueryAsync(AuditQueryFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _db.AuditEvents.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(filter.Actor))
        {
            query = query.Where(e => e.ActorSubjectId == filter.Actor);
        }
        if (!string.IsNullOrEmpty(filter.EntityType))
        {
            query = query.Where(e => e.EntityType == filter.EntityType);
        }
        if (!string.IsNullOrEmpty(filter.EntityId))
        {
            query = query.Where(e => e.EntityId == filter.EntityId);
        }
        if (!string.IsNullOrEmpty(filter.Action))
        {
            query = query.Where(e => e.Action == filter.Action);
        }
        if (filter.Cursor is { } afterSeq)
        {
            query = query.Where(e => e.Seq > afterSeq);
        }

        // Read the (potentially) filtered rows ordered by Seq ASC.
        // Timestamp bounds + the SQLite-incompatible DateTimeOffset
        // comparisons are applied client-side after the
        // server-side scan (matches the posture of the rest of the
        // codebase — see PolicyService list filters and the
        // OverrideExpiryReaper sweep).
        var rows = await query
            .OrderBy(e => e.Seq)
            .Take(filter.PageSize + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        IEnumerable<AuditEvent> filtered = rows;
        if (filter.From is { } from)
        {
            filtered = filtered.Where(e => e.Timestamp >= from);
        }
        if (filter.To is { } to)
        {
            filtered = filtered.Where(e => e.Timestamp <= to);
        }
        var page = filtered.ToList();

        // The peek-plus-one shape only holds when no client-side
        // timestamp filter trimmed rows. With a timestamp filter
        // we may have come back with fewer than pageSize matches
        // even though more rows exist further down the chain;
        // emit a cursor anyway based on the last *raw* row scanned
        // so the caller can resume.
        var more = rows.Count > filter.PageSize;
        var trim = more ? rows.Take(filter.PageSize).ToList() : rows;
        var trimmedFiltered = trim.AsEnumerable();
        if (filter.From is { } f) trimmedFiltered = trimmedFiltered.Where(e => e.Timestamp >= f);
        if (filter.To is { } t) trimmedFiltered = trimmedFiltered.Where(e => e.Timestamp <= t);
        var items = trimmedFiltered.Select(ToDto).ToList();

        long? nextCursor = more ? trim[^1].Seq : null;

        return new AuditPageDto(items, EncodeCursor(nextCursor), filter.PageSize);
    }

    /// <summary>
    /// Encodes a <see cref="long"/> seq boundary as an opaque
    /// base64-URL JSON string. Round-trips through
    /// <see cref="DecodeCursor"/>; tampering with a single
    /// character throws on decode and the controller turns that
    /// into a 400 ProblemDetails.
    /// </summary>
    public static string? EncodeCursor(long? afterSeq)
    {
        if (afterSeq is null) return null;
        var json = JsonSerializer.SerializeToUtf8Bytes(new { afterSeq });
        return Convert.ToBase64String(json);
    }

    /// <summary>
    /// Decodes a cursor produced by <see cref="EncodeCursor"/>.
    /// Returns <c>null</c> when <paramref name="cursor"/> is null
    /// or empty; throws <see cref="FormatException"/> on
    /// malformed input.
    /// </summary>
    public static long? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(cursor);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"Invalid cursor: {ex.Message}", ex);
        }
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("afterSeq", out var afterEl)
                || !afterEl.TryGetInt64(out var after))
            {
                throw new FormatException("Cursor missing afterSeq field.");
            }
            if (after < 1)
            {
                throw new FormatException("Cursor afterSeq must be >= 1.");
            }
            return after;
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Invalid cursor: {ex.Message}", ex);
        }
    }

    private static AuditEventDto ToDto(AuditEvent ev)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrEmpty(ev.FieldDiffJson) ? "[]" : ev.FieldDiffJson);
        return new(
            ev.Id,
            ev.Seq,
            Convert.ToHexString(ev.PrevHash).ToLowerInvariant(),
            Convert.ToHexString(ev.Hash).ToLowerInvariant(),
            ev.Timestamp,
            ev.ActorSubjectId,
            ev.ActorRoles,
            ev.Action,
            ev.EntityType,
            ev.EntityId,
            doc.RootElement.Clone(),
            ev.Rationale);
    }
}
