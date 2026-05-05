// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Api.Mcp.Authorization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Andy.Policies.Api.Mcp;

/// <summary>
/// MCP tools over the catalog audit chain (P6.7, story
/// rivoli-ai/andy-policies#48). Four tools —
/// <c>policy.audit.list</c>, <c>policy.audit.get</c>,
/// <c>policy.audit.verify</c>, <c>policy.audit.export</c> —
/// delegate to the same <see cref="IAuditQuery"/>,
/// <see cref="IAuditChain"/>, and <see cref="IAuditExporter"/>
/// powering REST (P6.5/P6.6). Following the established
/// <see cref="OverrideTools"/> contract: structured JSON DTOs
/// on success, prefixed error strings on failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Export shape.</b> <c>policy.audit.export</c> returns a
/// base64-encoded UTF-8 NDJSON bundle. MCP tools return text;
/// base64 keeps the bundle self-contained and integration-
/// neutral. Decoded content has <c>N</c> event lines (one per
/// audit row in range) plus a trailing summary line. The
/// bundle is verifiable offline by
/// <c>andy-policies-cli audit verify --file</c> (P6.5).
/// </para>
/// <para>
/// <b>RBAC posture.</b> Per-tool RBAC (<c>audit:verify</c>,
/// <c>audit:export</c>) is enforced via <see cref="McpRbacGuard"/>
/// since P7.6 (#64), mirroring the gRPC interceptor on
/// <c>AuditService</c>. Reads (<c>list</c>, <c>get</c>) remain
/// gated only by JWT auth at the MCP edge; the gRPC
/// surface is the canonical enforcement point for read-side.
/// </para>
/// </remarks>
[McpServerToolType]
public static class AuditTools
{
    private static readonly JsonSerializerOptions DtoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    [McpServerTool(Name = "policy.audit.list"), Description(
        "List audit events with optional filters: actor, entityType, entityId, " +
        "action, from / to (inclusive timestamps), cursor (opaque base64), " +
        "pageSize (1..500, default 50). Returns a JSON object with items, " +
        "nextCursor, and pageSize fields. fieldDiff is a parsed JSON Patch " +
        "array; hashes travel as lowercase hex.")]
    public static async Task<string> List(
        IAuditQuery query,
        [Description("Filter by actor subject id (exact match)")] string? actor = null,
        [Description("Filter by entity type, e.g. Policy, Override")] string? entityType = null,
        [Description("Filter by entity id (exact match)")] string? entityId = null,
        [Description("Filter by dotted action code, e.g. policy.update")] string? action = null,
        [Description("Inclusive lower timestamp bound, ISO 8601")] string? from = null,
        [Description("Inclusive upper timestamp bound, ISO 8601")] string? to = null,
        [Description("Opaque cursor from a previous page's nextCursor")] string? cursor = null,
        [Description("Rows per page; clamped 1..500")] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (pageSize < 1 || pageSize > 500)
        {
            return $"policy.audit.invalid_argument: pageSize must be in [1, 500]; got {pageSize}.";
        }
        DateTimeOffset? fromDt = null;
        if (!string.IsNullOrEmpty(from))
        {
            if (!DateTimeOffset.TryParse(from, out var parsed))
            {
                return $"policy.audit.invalid_argument: from '{from}' is not a valid ISO 8601 timestamp.";
            }
            fromDt = parsed;
        }
        DateTimeOffset? toDt = null;
        if (!string.IsNullOrEmpty(to))
        {
            if (!DateTimeOffset.TryParse(to, out var parsed))
            {
                return $"policy.audit.invalid_argument: to '{to}' is not a valid ISO 8601 timestamp.";
            }
            toDt = parsed;
        }
        if (fromDt is { } f && toDt is { } t && f > t)
        {
            return $"policy.audit.invalid_argument: from ({f:o}) must be <= to ({t:o}).";
        }

        long? cursorAfter;
        try
        {
            cursorAfter = Andy.Policies.Infrastructure.Audit.AuditQuery.DecodeCursor(cursor);
        }
        catch (FormatException)
        {
            return "policy.audit.invalid_argument: cursor is not a recognised base64-JSON token.";
        }

        var page = await query.QueryAsync(
            new AuditQueryFilter(actor, fromDt, toDt, entityType, entityId, action, cursorAfter, pageSize),
            ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(page, DtoJsonOptions);
    }

    [McpServerTool(Name = "policy.audit.get"), Description(
        "Get a single audit event by id. Returns " +
        "policy.audit.not_found when no row matches.")]
    public static async Task<string> Get(
        IAuditQuery query,
        [Description("Audit event id (GUID)")] string id,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var oid))
        {
            return $"policy.audit.invalid_argument: '{id}' is not a valid GUID.";
        }
        var dto = await query.GetAsync(oid, ct).ConfigureAwait(false);
        return dto is null
            ? $"policy.audit.not_found: AuditEvent {oid} not found."
            : JsonSerializer.Serialize(dto, DtoJsonOptions);
    }

    [McpServerTool(Name = "policy.audit.verify"), Description(
        "Verify the audit hash chain, optionally bounded to a [fromSeq, toSeq] " +
        "range. Returns a JSON object with valid, firstDivergenceSeq, " +
        "inspectedCount, and lastSeq. Divergence is a queryable state, not " +
        "an error — valid=false with firstDivergenceSeq pinpoints the " +
        "tampered row.")]
    [RbacGuard("andy-policies:audit:verify")]
    public static async Task<string> Verify(
        IAuditChain chain,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        [Description("Inclusive lower seq bound. Defaults to 1.")] long? fromSeq = null,
        [Description("Inclusive upper seq bound. Defaults to MAX(seq).")] long? toSeq = null,
        CancellationToken ct = default)
    {
        if (fromSeq is < 1)
        {
            return $"policy.audit.invalid_argument: fromSeq must be >= 1; got {fromSeq}.";
        }
        if (toSeq is < 1)
        {
            return $"policy.audit.invalid_argument: toSeq must be >= 1; got {toSeq}.";
        }
        if (fromSeq is { } f && toSeq is { } t && f > t)
        {
            return $"policy.audit.invalid_argument: fromSeq ({f}) must be <= toSeq ({t}).";
        }

        try
        {
            await McpRbacGuard.EnsureAsync(rbac, httpContext,
                "andy-policies:audit:verify", null, ct);
        }
        catch (McpAuthorizationException ex)
        {
            return $"policy.audit.forbidden: {ex.Reason}";
        }

        var result = await chain.VerifyChainAsync(fromSeq, toSeq, ct).ConfigureAwait(false);
        var dto = new ChainVerificationDto(
            result.Valid,
            result.FirstDivergenceSeq,
            result.InspectedCount,
            result.LastSeq);
        return JsonSerializer.Serialize(dto, DtoJsonOptions);
    }

    [McpServerTool(Name = "policy.audit.export"), Description(
        "Export the audit chain as a base64-encoded UTF-8 NDJSON bundle. " +
        "Decoded content has one JSON object per line for each audit event " +
        "(\"type\":\"event\") in [fromSeq, toSeq], followed by a single " +
        "summary line (\"type\":\"summary\") with count, terminalHashHex, " +
        "and exportedAt. The bundle is verifiable offline by " +
        "andy-policies-cli audit verify --file. Integrity is via the " +
        "embedded hash chain — no external KMS / detached signature.")]
    [RbacGuard("andy-policies:audit:export")]
    public static async Task<string> Export(
        IAuditExporter exporter,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        [Description("Inclusive lower seq bound. Defaults to 1.")] long? fromSeq = null,
        [Description("Inclusive upper seq bound. Defaults to MAX(seq).")] long? toSeq = null,
        CancellationToken ct = default)
    {
        if (fromSeq is < 1)
        {
            return $"policy.audit.invalid_argument: fromSeq must be >= 1; got {fromSeq}.";
        }
        if (toSeq is < 1)
        {
            return $"policy.audit.invalid_argument: toSeq must be >= 1; got {toSeq}.";
        }
        if (fromSeq is { } f && toSeq is { } t && f > t)
        {
            return $"policy.audit.invalid_argument: fromSeq ({f}) must be <= toSeq ({t}).";
        }

        try
        {
            await McpRbacGuard.EnsureAsync(rbac, httpContext,
                "andy-policies:audit:export", null, ct);
        }
        catch (McpAuthorizationException ex)
        {
            return $"policy.audit.forbidden: {ex.Reason}";
        }

        // Buffer in memory before base64-encoding for the MCP
        // response. For very large exports the gRPC surface (P6.8)
        // streams chunks; MCP's text-typed tool result requires
        // a single string, so callers should bound the range with
        // fromSeq/toSeq if memory is a concern.
        using var buffer = new MemoryStream();
        await exporter.WriteNdjsonAsync(buffer, fromSeq, toSeq, ct).ConfigureAwait(false);
        return Convert.ToBase64String(buffer.ToArray());
    }
}
