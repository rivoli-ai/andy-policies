// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Placeholder <see cref="IAuditWriter"/> until Epic P6 (audit hash chain,
/// rivoli-ai/andy-policies#6) replaces it with the real DB-backed
/// implementation. Logs each call at <c>Debug</c> so operators can confirm
/// the call sites are wired correctly even before P6 lands.
/// </summary>
public sealed class NoopAuditWriter : IAuditWriter
{
    private readonly ILogger<NoopAuditWriter> _log;

    public NoopAuditWriter(ILogger<NoopAuditWriter> log)
    {
        _log = log;
    }

    public Task AppendAsync(
        string action,
        Guid entityId,
        string actorSubjectId,
        string? rationale,
        CancellationToken ct = default)
    {
        _log.LogDebug(
            "Audit (no-op until P6): action={Action} entity={EntityId} actor={Actor} rationale={Rationale}",
            action, entityId, actorSubjectId, rationale ?? "(none)");
        return Task.CompletedTask;
    }
}
