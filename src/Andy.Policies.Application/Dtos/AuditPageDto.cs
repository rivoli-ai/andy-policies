// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Cursor-paginated response from <c>GET /api/audit</c> (P6.6,
/// story rivoli-ai/andy-policies#46). Cursor pagination is
/// chosen over offset because the audit table grows
/// monotonically — offset windows would shift under concurrent
/// inserts, and skipping the head of a million-row table is a
/// scan, not a seek.
/// </summary>
/// <param name="Items">Audit events for this page, ordered by
/// <c>Seq</c> ascending.</param>
/// <param name="NextCursor">Opaque base64-URL encoded boundary
/// for the next page. Null when no more rows match the
/// filter.</param>
/// <param name="PageSize">Echo of the requested page size.
/// Useful for clients that paginate based on the server's
/// honoured value (e.g. when the request omits the parameter
/// and gets the default).</param>
public sealed record AuditPageDto(
    IReadOnlyList<AuditEventDto> Items,
    string? NextCursor,
    int PageSize);
