// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Cursor-paginated query over the catalog audit chain (P6.6,
/// story rivoli-ai/andy-policies#46). Backs <c>GET /api/audit</c>
/// and the upcoming MCP / gRPC list/get surfaces (P6.7+).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why cursor pagination?</b> The audit table is append-only
/// and grows monotonically. Offset pagination over such a table
/// (a) shifts the page window every time a new row arrives mid-
/// iteration, and (b) requires a scan over the skipped rows,
/// which is O(N) on a million-row table. Cursor pagination on
/// <c>Seq</c> reads from a B-tree seek — O(log N) — and rows
/// arriving after the cursor was issued show up on the next
/// page rather than disrupting the current one.
/// </para>
/// <para>
/// <b>Filter semantics.</b> All non-null fields are AND'd
/// together. Timestamp bounds are inclusive on both ends.
/// </para>
/// </remarks>
public interface IAuditQuery
{
    /// <summary>
    /// Returns one page of audit events matching
    /// <paramref name="filter"/>. The result's
    /// <see cref="AuditPageDto.NextCursor"/> is non-null iff
    /// there are more rows after this page.
    /// </summary>
    Task<AuditPageDto> QueryAsync(AuditQueryFilter filter, CancellationToken ct);

    /// <summary>
    /// Returns a single event by its
    /// <see cref="Domain.Entities.AuditEvent.Id"/>, or
    /// <c>null</c> when no row matches. P6.7's MCP
    /// <c>policy.audit.get</c> tool delegates here.
    /// </summary>
    Task<AuditEventDto?> GetAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Inputs to <see cref="IAuditQuery.QueryAsync"/>. Every field is
/// optional; the controller layer enforces the
/// <see cref="PageSize"/> bounds (1..500) before constructing
/// this record.
/// </summary>
/// <param name="Actor">Filter by <c>ActorSubjectId</c> exact
/// match.</param>
/// <param name="From">Inclusive lower timestamp bound.</param>
/// <param name="To">Inclusive upper timestamp bound.</param>
/// <param name="EntityType">Filter by <c>EntityType</c> exact
/// match (e.g. <c>Policy</c>, <c>Override</c>).</param>
/// <param name="EntityId">Filter by <c>EntityId</c> exact match;
/// best paired with <see cref="EntityType"/> so the composite
/// index from P6.1 covers the lookup.</param>
/// <param name="Action">Filter by <c>Action</c> exact match
/// (e.g. <c>policy.version.publish</c>).</param>
/// <param name="Cursor">Decoded cursor ID from a previous
/// page's <see cref="AuditPageDto.NextCursor"/>. Caller-side
/// errors (malformed cursor, base64 corruption) are surfaced
/// as 400 ProblemDetails by the controller; the service-layer
/// contract assumes a valid cursor or null.</param>
/// <param name="PageSize">Number of rows to return (clamped
/// 1..500 by the controller).</param>
public sealed record AuditQueryFilter(
    string? Actor,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? EntityType,
    string? EntityId,
    string? Action,
    long? Cursor,
    int PageSize);
