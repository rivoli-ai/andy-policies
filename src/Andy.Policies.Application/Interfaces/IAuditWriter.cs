// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Append-only audit writer hook (P3.2, story rivoli-ai/andy-policies#20).
/// Per-mutation services call this to record an audit row; the real
/// hash-chained implementation lands in Epic P6
/// (rivoli-ai/andy-policies#6). A no-op implementation in Infrastructure
/// keeps DI satisfied until then.
/// </summary>
public interface IAuditWriter
{
    /// <summary>
    /// Append an audit envelope. <paramref name="action"/> is a stable
    /// dotted identifier such as <c>"binding.created"</c> or
    /// <c>"binding.deleted"</c>; <paramref name="entityId"/> is the row id
    /// being mutated; <paramref name="rationale"/> is required by P6 for
    /// transitions but optional for plain creates/deletes.
    /// </summary>
    Task AppendAsync(
        string action,
        Guid entityId,
        string actorSubjectId,
        string? rationale,
        CancellationToken ct = default);
}
