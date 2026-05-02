// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Infrastructure.Audit;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace Andy.Policies.Api.GrpcServices;

/// <summary>
/// gRPC surface for the catalog audit chain (P6.8, story
/// rivoli-ai/andy-policies#50). Four RPCs delegate to the same
/// <see cref="IAuditQuery"/>, <see cref="IAuditChain"/>, and
/// <see cref="IAuditExporter"/> powering REST (P6.5/P6.6) and
/// MCP (P6.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Streaming export.</b> <c>ExportAudit</c> is a server-
/// streaming RPC; the response is a sequence of
/// <c>AuditExportChunk</c>s carrying contiguous slices of the
/// UTF-8 NDJSON byte stream. Concatenating the chunks yields
/// the same bundle byte-for-byte that <c>policy.audit.export</c>
/// (MCP) returns. Chunks are at least 16 KiB except for the
/// final tail.
/// </para>
/// <para>
/// <b>Status mapping.</b> <c>NOT_FOUND</c> for missing ids;
/// <c>INVALID_ARGUMENT</c> for bad GUIDs / out-of-range page
/// sizes / inverted ranges / malformed cursors;
/// <c>FAILED_PRECONDITION</c> for divergence is intentionally
/// <i>not</i> used — divergence is a queryable state on the
/// success channel (matches the REST contract).
/// </para>
/// </remarks>
[Authorize]
public class AuditGrpcService : Andy.Policies.Api.Protos.AuditService.AuditServiceBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 500;
    private const int ExportChunkBytes = 16 * 1024;

    private readonly IAuditQuery _query;
    private readonly IAuditChain _chain;
    private readonly IAuditExporter _exporter;

    public AuditGrpcService(IAuditQuery query, IAuditChain chain, IAuditExporter exporter)
    {
        _query = query;
        _chain = chain;
        _exporter = exporter;
    }

    public override async Task<ListAuditResponse> ListAudit(
        ListAuditRequest request, ServerCallContext context)
    {
        var size = request.PageSize <= 0 ? DefaultPageSize : request.PageSize;
        if (size < 1 || size > MaxPageSize)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"page_size must be in [1, {MaxPageSize}]; got {size}."));
        }

        DateTimeOffset? from = ParseTimestamp(request.From, "from", out var fromErr);
        if (fromErr is not null) throw new RpcException(new Status(StatusCode.InvalidArgument, fromErr));
        DateTimeOffset? to = ParseTimestamp(request.To, "to", out var toErr);
        if (toErr is not null) throw new RpcException(new Status(StatusCode.InvalidArgument, toErr));
        if (from is { } f && to is { } t && f > t)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"from ({f:o}) must be <= to ({t:o})."));
        }

        long? cursorAfter;
        try
        {
            cursorAfter = AuditQuery.DecodeCursor(string.IsNullOrEmpty(request.Cursor) ? null : request.Cursor);
        }
        catch (FormatException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"cursor: {ex.Message}"));
        }

        var page = await _query.QueryAsync(
            new AuditQueryFilter(
                Actor: NullIfEmpty(request.Actor),
                From: from,
                To: to,
                EntityType: NullIfEmpty(request.EntityType),
                EntityId: NullIfEmpty(request.EntityId),
                Action: NullIfEmpty(request.Action),
                Cursor: cursorAfter,
                PageSize: size),
            context.CancellationToken).ConfigureAwait(false);

        var response = new ListAuditResponse
        {
            NextCursor = page.NextCursor ?? string.Empty,
            PageSize = page.PageSize,
        };
        foreach (var ev in page.Items)
        {
            response.Items.Add(ToProto(ev));
        }
        return response;
    }

    public override async Task<AuditEventMessage> GetAudit(
        GetAuditRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var id))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"id '{request.Id}' is not a valid GUID."));
        }
        var dto = await _query.GetAsync(id, context.CancellationToken).ConfigureAwait(false);
        if (dto is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"AuditEvent {id} not found."));
        }
        return ToProto(dto);
    }

    public override async Task<VerifyAuditResponse> VerifyAudit(
        VerifyAuditRequest request, ServerCallContext context)
    {
        long? from = request.FromSeq <= 0 ? null : request.FromSeq;
        long? to = request.ToSeq <= 0 ? null : request.ToSeq;
        if (from is { } f && to is { } t && f > t)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"from_seq ({f}) must be <= to_seq ({t})."));
        }

        var result = await _chain.VerifyChainAsync(from, to, context.CancellationToken)
            .ConfigureAwait(false);
        return new VerifyAuditResponse
        {
            Valid = result.Valid,
            FirstDivergenceSeq = result.FirstDivergenceSeq ?? 0,
            InspectedCount = result.InspectedCount,
            LastSeq = result.LastSeq,
        };
    }

    public override async Task ExportAudit(
        ExportAuditRequest request,
        IServerStreamWriter<AuditExportChunk> response,
        ServerCallContext context)
    {
        long? from = request.FromSeq <= 0 ? null : request.FromSeq;
        long? to = request.ToSeq <= 0 ? null : request.ToSeq;
        if (from is { } f && to is { } t && f > t)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"from_seq ({f}) must be <= to_seq ({t})."));
        }

        // Buffer the export in memory then stream chunks. The
        // streaming-into-pipe approach in the issue spec works
        // but adds threading complexity for marginal benefit at
        // current chain sizes; this keeps the wire format
        // identical (chunked NDJSON) while sidestepping pipe
        // back-pressure edge cases. Future stories can swap to
        // a pipe if exports grow large enough that buffering
        // matters.
        await using var buffer = new MemoryStream();
        await _exporter.WriteNdjsonAsync(buffer, from, to, context.CancellationToken)
            .ConfigureAwait(false);

        var bytes = buffer.ToArray();
        var offset = 0;
        while (offset < bytes.Length)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var len = Math.Min(ExportChunkBytes, bytes.Length - offset);
            var chunk = new AuditExportChunk
            {
                Ndjson = ByteString.CopyFrom(bytes, offset, len),
            };
            await response.WriteAsync(chunk, context.CancellationToken).ConfigureAwait(false);
            offset += len;
        }
    }

    // -- helpers --------------------------------------------------------------

    private static AuditEventMessage ToProto(AuditEventDto dto)
    {
        var rolesList = dto.ActorRoles.ToArray();
        var msg = new AuditEventMessage
        {
            Id = dto.Id.ToString(),
            Seq = dto.Seq,
            PrevHashHex = dto.PrevHashHex,
            HashHex = dto.HashHex,
            Timestamp = dto.Timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ActorSubjectId = dto.ActorSubjectId,
            Action = dto.Action,
            EntityType = dto.EntityType,
            EntityId = dto.EntityId,
            FieldDiffJson = dto.FieldDiff.GetRawText(),
            Rationale = dto.Rationale ?? string.Empty,
        };
        msg.ActorRoles.AddRange(rolesList);
        return msg;
    }

    private static DateTimeOffset? ParseTimestamp(string raw, string field, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(raw)) return null;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }
        error = $"{field} '{raw}' is not a valid ISO 8601 timestamp.";
        return null;
    }

    private static string? NullIfEmpty(string raw) =>
        string.IsNullOrEmpty(raw) ? null : raw;
}
