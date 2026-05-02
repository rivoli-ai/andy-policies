// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Streaming NDJSON exporter for the catalog audit chain (P6.7,
/// story rivoli-ai/andy-policies#48). Backs MCP
/// <c>policy.audit.export</c> and the upcoming gRPC export
/// surface (P6.8). Output format:
/// <list type="number">
///   <item>One JSON object per line for each
///     <c>AuditEvent</c> in <c>[fromSeq, toSeq]</c>, in
///     ascending <c>Seq</c> order; each line carries
///     <c>"type":"event"</c>.</item>
///   <item>A trailing summary line with <c>"type":"summary"</c>,
///     <c>fromSeq</c>, <c>toSeq</c>, <c>count</c>,
///     <c>genesisPrevHashHex</c>, <c>terminalHashHex</c>,
///     <c>exportedAt</c>.</item>
/// </list>
/// The bundle is verifiable offline by
/// <c>andy-policies-cli audit verify --file</c> (P6.5);
/// integrity rests on the embedded hash chain — there is no
/// external KMS / detached signature in v1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streaming.</b> The implementation must not load the entire
/// chain into memory; <c>AsAsyncEnumerable</c> + a
/// <see cref="StreamWriter"/> on the caller-supplied output
/// stream keeps peak heap usage bounded regardless of chain
/// size. Tests assert export of 1,000 events stays under a
/// pragmatic memory budget.
/// </para>
/// </remarks>
public interface IAuditExporter
{
    /// <summary>
    /// Streams the audit chain as NDJSON to
    /// <paramref name="output"/>. Caller owns
    /// <paramref name="output"/> — the implementation flushes
    /// before returning but does not dispose the stream.
    /// </summary>
    Task WriteNdjsonAsync(
        Stream output,
        long? fromSeq,
        long? toSeq,
        CancellationToken ct);
}
