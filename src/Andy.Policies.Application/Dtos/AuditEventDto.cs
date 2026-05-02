// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Wire-format projection of <c>Andy.Policies.Domain.Entities.AuditEvent</c>
/// (P6.2, story rivoli-ai/andy-policies#42). Hashes travel as
/// lowercase hex strings so REST/MCP/gRPC consumers don't have to
/// agree on a binary encoding; the binary form stays in the
/// database column.
/// </summary>
/// <param name="Id">Stable random GUID assigned at write time.</param>
/// <param name="Seq">DB-assigned monotonic sequence number; chain
/// verification walks rows ordered by this column.</param>
/// <param name="PrevHashHex">Lowercase hex of the previous row's
/// <c>Hash</c>; 64 zero chars for the genesis row.</param>
/// <param name="HashHex">Lowercase hex of
/// <c>SHA-256(prevHash || canonicalJson(payload))</c>.</param>
/// <param name="Timestamp">UTC instant the event was written.</param>
/// <param name="ActorSubjectId">JWT <c>sub</c> claim of the actor.</param>
/// <param name="ActorRoles">Snapshot of actor RBAC roles.</param>
/// <param name="Action">Dotted action code (e.g.
/// <c>policy.version.publish</c>).</param>
/// <param name="EntityType">Canonical type of the mutated entity.</param>
/// <param name="EntityId">String form of the mutated entity's
/// primary key.</param>
/// <param name="FieldDiff">Parsed RFC 6902 JSON Patch document.
/// Travels on the wire as a JSON array (P6.6, story
/// rivoli-ai/andy-policies#46) — not as a JSON-encoded string —
/// so consumers can <c>jq '.fieldDiff[0].op'</c> directly without
/// a second parse. Defaults to <c>[]</c> for create / delete
/// events whose diff is implicit.</param>
/// <param name="Rationale">Free-text rationale supplied by the
/// actor; nullable when the rationale-required filter is off.</param>
public sealed record AuditEventDto(
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
    JsonElement FieldDiff,
    string? Rationale);
