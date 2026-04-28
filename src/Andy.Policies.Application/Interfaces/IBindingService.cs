// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Application service for <c>Binding</c> mutation and query (P3.2, story
/// rivoli-ai/andy-policies#20). REST (P3.3), MCP (P3.5), gRPC (P3.6), and
/// CLI (P3.7) all delegate to this single interface — controllers and
/// tools never duplicate validation, retired-version refusal, or
/// soft-delete logic.
/// </summary>
public interface IBindingService
{
    /// <summary>
    /// Create a new binding. Throws
    /// <see cref="Exceptions.NotFoundException"/> when the target version
    /// does not exist, <see cref="Exceptions.BindingRetiredVersionException"/>
    /// when the target version is Retired, and
    /// <see cref="Exceptions.ValidationException"/> on empty / oversized
    /// <c>TargetRef</c>.
    /// </summary>
    Task<BindingDto> CreateAsync(
        CreateBindingRequest request,
        string actorSubjectId,
        CancellationToken ct = default);

    /// <summary>
    /// Soft-delete the binding. Stamps <c>DeletedAt</c> and
    /// <c>DeletedBySubjectId</c> rather than removing the row so P6's audit
    /// chain has an append-only history. Throws
    /// <see cref="Exceptions.NotFoundException"/> when the binding does not
    /// exist or is already deleted.
    /// </summary>
    Task DeleteAsync(
        Guid bindingId,
        string actorSubjectId,
        string? rationale,
        CancellationToken ct = default);

    /// <summary>Get a single binding by id, or <c>null</c> if not found.</summary>
    Task<BindingDto?> GetAsync(Guid bindingId, CancellationToken ct = default);

    /// <summary>
    /// List all bindings against a given <c>PolicyVersion</c> ordered by
    /// most-recently-created first. <paramref name="includeDeleted"/>
    /// controls whether tombstoned rows are returned.
    /// </summary>
    Task<IReadOnlyList<BindingDto>> ListByPolicyVersionAsync(
        Guid policyVersionId,
        bool includeDeleted,
        CancellationToken ct = default);

    /// <summary>
    /// List active (non-deleted) bindings for a given target, ordered by
    /// most-recently-created first. Match is exact-equality on
    /// <c>(TargetType, TargetRef)</c> — no prefix or case-folding —
    /// because consumer-side resolution semantics rely on byte-exact match.
    /// </summary>
    Task<IReadOnlyList<BindingDto>> ListByTargetAsync(
        BindingTargetType targetType,
        string targetRef,
        CancellationToken ct = default);
}
