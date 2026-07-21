// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Append-only mutation audit adapter. Production DI maps this interface
/// to the tamper-evident <see cref="IAuditChain"/> implementation; callers
/// append inside the same database transaction as the catalog mutation.
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
