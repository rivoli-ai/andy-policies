// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Andy.Policies.Infrastructure.Audit;

/// <summary>
/// EF-backed <see cref="IAuditChain"/> implementation (P6.2, story
/// rivoli-ai/andy-policies#42). Every <see cref="AppendAsync"/>
/// call serialises tail-read + insert behind a serializable
/// transaction; on Postgres a session-level
/// <c>pg_advisory_xact_lock(7412001)</c> guarantees a global FIFO
/// across processes (the reaper, the API, the seeder), and on
/// SQLite a process-wide <see cref="SemaphoreSlim"/> stands in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hash envelope.</b> The bytes that feed SHA-256 are
/// <c>prevHash || canonicalJson(payload)</c>, where the payload is
/// a closed JSON shape (Id, ISO 8601 timestamp, ActorSubjectId,
/// sorted ActorRoles, Action, EntityType, EntityId, FieldDiff
/// (parsed as JSON, not as a string), Rationale). The canonical
/// JSON algorithm is implemented in <see cref="CanonicalJson"/>
/// and pinned in ADR 0006 (P6.9, #54).
/// </para>
/// <para>
/// <b>Atomicity contract.</b> Callers should invoke
/// <see cref="AppendAsync"/> inside the same outer DbContext
/// transaction as their state change. The chain opens a nested
/// serializable transaction when no ambient one exists; when an
/// ambient transaction is present it joins it (the EF default) so
/// the audit row commits or rolls back with the caller's mutation.
/// </para>
/// </remarks>
public sealed class AuditChain : IAuditChain
{
    /// <summary>App-wide advisory-lock id for the audit-append lock
    /// on Postgres. Constant so every process / replica targets the
    /// same session. The 7-digit value is chosen to be visually
    /// distinct from common module-id ranges; see ADR 0006.</summary>
    public const long PostgresAdvisoryLockId = 7412001;

    private static readonly byte[] Genesis = new byte[32];

    /// <summary>Process-wide gate for the SQLite path. SQLite has
    /// no advisory locks; serializing in-process keeps concurrent
    /// appenders from racing on tail-read. Multi-process SQLite
    /// is not a supported deployment for andy-policies (embedded
    /// mode is single-process by design).</summary>
    private static readonly SemaphoreSlim SqliteGate = new(1, 1);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public AuditChain(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<AuditEventDto> AppendAsync(AuditAppendRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Action);
        ArgumentException.ThrowIfNullOrEmpty(request.EntityType);
        ArgumentException.ThrowIfNullOrEmpty(request.EntityId);
        ArgumentException.ThrowIfNullOrEmpty(request.ActorSubjectId);
        ArgumentNullException.ThrowIfNull(request.ActorRoles);

        var diffJson = string.IsNullOrEmpty(request.FieldDiffJson) ? "[]" : request.FieldDiffJson;

        var isNpgsql = _db.Database.IsNpgsql();
        var ambient = _db.Database.CurrentTransaction;

        if (isNpgsql)
        {
            // Honour an ambient transaction (the typical caller
            // pattern: BindingService etc. open one for state +
            // audit atomicity); only open our own when none exists.
            //
            // Isolation level: READ COMMITTED, not SERIALIZABLE.
            // The pg_advisory_xact_lock below provides the only
            // ordering guarantee we need (global FIFO across all
            // appenders). SERIALIZABLE on top of the lock is an
            // anti-pattern — the SSI predicate detector trips on
            // tail-read snapshots that the lock has already
            // serialised, surfacing as spurious 40001 errors
            // under contention. READ COMMITTED + advisory lock is
            // the canonical recipe.
            if (ambient is null)
            {
                await using var tx = await _db.Database
                    .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
                    .ConfigureAwait(false);
                var dto = await AppendUnderPostgresLockAsync(request, diffJson, ct)
                    .ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return dto;
            }

            return await AppendUnderPostgresLockAsync(request, diffJson, ct).ConfigureAwait(false);
        }

        // SQLite path: serialise in-process via the static gate.
        // BeginImmediate is the SQLite equivalent of a write lock.
        await SqliteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ambient is null)
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct)
                    .ConfigureAwait(false);
                var dto = await AppendCoreAsync(request, diffJson, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return dto;
            }
            return await AppendCoreAsync(request, diffJson, ct).ConfigureAwait(false);
        }
        finally
        {
            SqliteGate.Release();
        }
    }

    private async Task<AuditEventDto> AppendUnderPostgresLockAsync(
        AuditAppendRequest request, string diffJson, CancellationToken ct)
    {
        // pg_advisory_xact_lock is auto-released at COMMIT/ROLLBACK
        // — no explicit unlock path needed. Single global key means
        // every appender serialises behind one another even across
        // processes (API instances, reaper, seeder).
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({PostgresAdvisoryLockId})", ct)
            .ConfigureAwait(false);
        return await AppendCoreAsync(request, diffJson, ct).ConfigureAwait(false);
    }

    private async Task<AuditEventDto> AppendCoreAsync(
        AuditAppendRequest request, string diffJson, CancellationToken ct)
    {
        // Read the chain tail (a single row) under the advisory lock
        // / write txn so no concurrent appender can produce a
        // sibling between the read and our insert. Seq is assigned
        // here — neither bigserial nor SQLite AUTOINCREMENT, because
        // (a) Postgres bigserial would race past the lock since the
        // sequence is independent of advisory locks, and (b) SQLite
        // doesn't AUTOINCREMENT a non-PK column. Writer-assigned
        // monotonic Seq under the lock is the only contract that's
        // consistent across both providers.
        var tail = await _db.AuditEvents.AsNoTracking()
            .OrderByDescending(e => e.Seq)
            .Select(e => new { e.Seq, e.Hash })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var prevHash = tail?.Hash ?? Genesis;
        var nextSeq = (tail?.Seq ?? 0) + 1;

        var id = Guid.NewGuid();
        var timestamp = _clock.GetUtcNow();
        var roles = request.ActorRoles.ToArray();
        var hash = ComputeHash(
            prevHash,
            id,
            timestamp,
            request.ActorSubjectId,
            roles,
            request.Action,
            request.EntityType,
            request.EntityId,
            diffJson,
            request.Rationale);

        var ev = new AuditEvent
        {
            Id = id,
            Seq = nextSeq,
            Timestamp = timestamp,
            ActorSubjectId = request.ActorSubjectId,
            ActorRoles = roles,
            Action = request.Action,
            EntityType = request.EntityType,
            EntityId = request.EntityId,
            FieldDiffJson = diffJson,
            Rationale = request.Rationale,
            PrevHash = prevHash,
            Hash = hash,
        };

        _db.AuditEvents.Add(ev);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return ToDto(ev);
    }

    public async Task<ChainVerificationResult> VerifyChainAsync(
        long? fromSeq, long? toSeq, CancellationToken ct)
    {
        var lower = fromSeq is { } f && f > 1 ? f : 1;

        // Seed `prev` from the row before the lower bound when the
        // verifier is asked for a partial range. When lower == 1,
        // the genesis convention applies (32 zero bytes).
        byte[] prev;
        if (lower > 1)
        {
            var prior = await _db.AuditEvents.AsNoTracking()
                .Where(e => e.Seq == lower - 1)
                .Select(e => e.Hash)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (prior is null)
            {
                // The caller asked to start at fromSeq but the prior
                // row doesn't exist — the chain has a gap below
                // them, which is itself a divergence.
                return new ChainVerificationResult(false, lower, 0, 0);
            }
            prev = prior;
        }
        else
        {
            prev = Genesis;
        }

        var query = _db.AuditEvents.AsNoTracking()
            .Where(e => e.Seq >= lower);
        if (toSeq is { } t)
        {
            query = query.Where(e => e.Seq <= t);
        }
        var rows = await query.OrderBy(e => e.Seq).ToListAsync(ct).ConfigureAwait(false);

        long inspected = 0;
        long lastSeq = lower > 1 ? lower - 1 : 0;
        foreach (var row in rows)
        {
            inspected++;
            lastSeq = row.Seq;

            if (!ByteEquals(row.PrevHash, prev))
            {
                return new ChainVerificationResult(false, row.Seq, inspected, lastSeq);
            }

            var recomputed = ComputeHash(
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
            if (!ByteEquals(row.Hash, recomputed))
            {
                return new ChainVerificationResult(false, row.Seq, inspected, lastSeq);
            }

            prev = row.Hash;
        }

        return new ChainVerificationResult(true, null, inspected, lastSeq);
    }

    /// <summary>
    /// Computes <c>SHA-256(prevHash || canonicalJson(payload))</c>
    /// where <c>payload</c> is the closed audit envelope. Public
    /// so unit tests can pin golden vectors without going through
    /// the full append path.
    /// </summary>
    public static byte[] ComputeHash(
        byte[] prevHash,
        Guid id,
        DateTimeOffset timestamp,
        string actorSubjectId,
        IReadOnlyList<string> actorRoles,
        string action,
        string entityType,
        string entityId,
        string fieldDiffJson,
        string? rationale)
    {
        // Embed the patch document as parsed JSON, not as a string —
        // serialising it as a string would re-quote and miss the
        // canonicalisation. JsonDocument must be wrapped in `using`
        // to keep the underlying buffer alive while we read.
        using var diffDoc = JsonDocument.Parse(string.IsNullOrEmpty(fieldDiffJson) ? "[]" : fieldDiffJson);

        // Build the envelope as an in-memory JSON tree so
        // CanonicalJson sorts keys and writes deterministic bytes.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("action", action);
            writer.WritePropertyName("actorRoles");
            writer.WriteStartArray();
            // Sort actorRoles lex order so insertion order doesn't
            // change the hash. The canonicaliser also preserves
            // array order, but pre-sorting bakes the contract into
            // the input itself.
            foreach (var r in actorRoles.OrderBy(s => s, StringComparer.Ordinal))
            {
                writer.WriteStringValue(r);
            }
            writer.WriteEndArray();
            writer.WriteString("actorSubjectId", actorSubjectId);
            writer.WriteString("entityId", entityId);
            writer.WriteString("entityType", entityType);
            writer.WritePropertyName("fieldDiff");
            diffDoc.RootElement.WriteTo(writer);
            writer.WriteString("id", id.ToString());
            if (rationale is null)
            {
                writer.WriteNull("rationale");
            }
            else
            {
                writer.WriteString("rationale", rationale);
            }
            writer.WriteString("timestamp",
                timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        var canonical = CanonicalJson.Serialize(doc.RootElement);

        // SHA-256 of (prevHash || canonical).
        using var sha = SHA256.Create();
        sha.TransformBlock(prevHash, 0, prevHash.Length, null, 0);
        sha.TransformFinalBlock(canonical, 0, canonical.Length);
        return sha.Hash!;
    }

    private static bool ByteEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static AuditEventDto ToDto(AuditEvent ev) => new(
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
        ev.FieldDiffJson,
        ev.Rationale);
}
