// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Entities;

/// <summary>
/// One row in the tamper-evident catalog audit chain (P6.1, story
/// rivoli-ai/andy-policies#41). Every mutation in andy-policies —
/// policy create/edit/publish/transition, binding create/delete,
/// scope create/delete, override propose/approve/revoke/expire —
/// inserts exactly one <see cref="AuditEvent"/>; reads are not
/// audited. The chain is the single source of truth for "who did
/// what, when, with what rationale" downstream of P5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only at the row level.</b> All properties are
/// <c>init</c>-only — once a row reaches the database it is
/// immutable. The migration (P6.1) emits provider-specific
/// triggers + role grants that reject UPDATE/DELETE so the
/// invariant survives even if a SQL-injection chain bypasses
/// the application code (see ADR 0006 for the threat model).
/// </para>
/// <para>
/// <b>Hash chain.</b> <see cref="PrevHash"/> + <see cref="Hash"/>
/// land here as 32-byte SHA-256 outputs; the linking algorithm
/// (<c>hash[n] = SHA-256(hash[n-1] || canonicalJson(payload[n]))</c>)
/// is implemented by P6.2 <c>IAuditChain</c>. Genesis convention:
/// the first event's <c>PrevHash</c> is 32 zero bytes.
/// </para>
/// <para>
/// <b>Field diff.</b> <see cref="FieldDiffJson"/> stores an
/// RFC 6902 JSON Patch document (P6.3 generator) describing the
/// before-after delta of the mutated entity. Always non-null;
/// defaults to <c>"[]"</c> for create / delete events whose diff
/// is implicit in the action.
/// </para>
/// </remarks>
public sealed class AuditEvent
{
    /// <summary>Random GUID assigned at write time. Stable
    /// external identifier; not used for ordering.</summary>
    public Guid Id { get; init; }

    /// <summary>Monotonic, global sequence number assigned by the
    /// database (Postgres <c>bigserial</c>; SQLite
    /// <c>INTEGER PRIMARY KEY AUTOINCREMENT</c>). The chain order
    /// is canonical: P6.2's verifier walks rows ordered by
    /// <c>Seq</c>.</summary>
    public long Seq { get; init; }

    /// <summary>SHA-256 of the previous row's <see cref="Hash"/>;
    /// 32 zero bytes for the genesis row. Stored as <c>bytea</c>
    /// on Postgres, <c>BLOB</c> on SQLite.</summary>
    public byte[] PrevHash { get; init; } = new byte[32];

    /// <summary>SHA-256 over <c>PrevHash || canonicalJson(payload)</c>
    /// where <c>payload</c> is the canonicalised audit envelope
    /// (P6.9 ADR pins the canonical JSON algorithm).</summary>
    public byte[] Hash { get; init; } = new byte[32];

    /// <summary>UTC instant the event was written. ISO 8601 with
    /// millisecond precision when serialised; the canonicaliser
    /// normalises before hashing.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>JWT <c>sub</c> claim of the actor (e.g.
    /// <c>user:42</c>, <c>system:reaper</c>).</summary>
    public string ActorSubjectId { get; init; } = string.Empty;

    /// <summary>Snapshot of the actor's RBAC roles at write time.
    /// Captured as a frozen array so the audit row stays
    /// meaningful even if the actor's grants change later.</summary>
    public string[] ActorRoles { get; init; } = Array.Empty<string>();

    /// <summary>Dotted action code (e.g.
    /// <c>policy.version.publish</c>, <c>override.approve</c>).
    /// Each writer decides the canonical action name; the audit
    /// log filters on this field.</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>Canonical type of the entity that was mutated
    /// (e.g. <c>PolicyVersion</c>, <c>Binding</c>,
    /// <c>Override</c>). Used together with
    /// <see cref="EntityId"/> for entity-scoped queries.</summary>
    public string EntityType { get; init; } = string.Empty;

    /// <summary>String form of the mutated row's primary key.
    /// Stays a string so non-Guid keys (composite refs etc.)
    /// can travel through the same column.</summary>
    public string EntityId { get; init; } = string.Empty;

    /// <summary>RFC 6902 JSON Patch document describing the
    /// before-after delta. Always non-null; <c>"[]"</c> for
    /// create / delete events whose diff is implicit. The
    /// generator that produces this lives in P6.3.</summary>
    public string FieldDiffJson { get; init; } = "[]";

    /// <summary>Free-text rationale supplied by the actor. The
    /// rationale-required filter (P6.4) gates the writer behind
    /// <c>andy.policies.rationaleRequired</c>; null is permitted
    /// only when the toggle is off.</summary>
    public string? Rationale { get; init; }
}
