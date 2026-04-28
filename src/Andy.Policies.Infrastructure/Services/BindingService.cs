// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Application-layer service for <c>Binding</c> mutation and query (P3.2,
/// story rivoli-ai/andy-policies#20). All four parity surfaces (REST P3.3,
/// MCP P3.5, gRPC P3.6, CLI P3.7) delegate here so retired-version
/// refusal, soft-delete semantics, and target-side query shape stay
/// uniform.
/// </summary>
public sealed class BindingService : IBindingService
{
    private const int MaxTargetRefLength = 512;

    private readonly AppDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;

    public BindingService(AppDbContext db, IAuditWriter audit, TimeProvider clock)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
    }

    public async Task<BindingDto> CreateAsync(
        CreateBindingRequest request,
        string actorSubjectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(actorSubjectId);

        var targetRef = (request.TargetRef ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(targetRef))
        {
            throw new ValidationException("TargetRef is required and may not be empty or whitespace.");
        }
        if (targetRef.Length > MaxTargetRefLength)
        {
            throw new ValidationException(
                $"TargetRef length {targetRef.Length} exceeds the {MaxTargetRefLength}-char limit.");
        }

        // Reject bindings to a Retired version. Active and WindingDown are
        // bindable — only Retired refuses (the P2 lifecycle guard for P3).
        var version = await _db.PolicyVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == request.PolicyVersionId, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                $"PolicyVersion {request.PolicyVersionId} not found.");
        if (version.State == LifecycleState.Retired)
        {
            throw new BindingRetiredVersionException(version.Id);
        }

        var binding = new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = version.Id,
            TargetType = request.TargetType,
            TargetRef = targetRef,
            BindStrength = request.BindStrength,
            CreatedAt = _clock.GetUtcNow(),
            CreatedBySubjectId = actorSubjectId,
        };
        _db.Bindings.Add(binding);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.AppendAsync(
            "binding.created", binding.Id, actorSubjectId, rationale: null, ct)
            .ConfigureAwait(false);

        return ToDto(binding);
    }

    public async Task DeleteAsync(
        Guid bindingId,
        string actorSubjectId,
        string? rationale,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(actorSubjectId);

        var binding = await _db.Bindings
            .FirstOrDefaultAsync(b => b.Id == bindingId, ct)
            .ConfigureAwait(false);
        if (binding is null || binding.DeletedAt is not null)
        {
            // Already-deleted is treated as not-found so callers can't
            // distinguish "never existed" from "tombstoned" — both mean
            // "no live binding by this id".
            throw new NotFoundException($"Binding {bindingId} not found.");
        }

        binding.DeletedAt = _clock.GetUtcNow();
        binding.DeletedBySubjectId = actorSubjectId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.AppendAsync(
            "binding.deleted", binding.Id, actorSubjectId, rationale, ct)
            .ConfigureAwait(false);
    }

    public async Task<BindingDto?> GetAsync(Guid bindingId, CancellationToken ct = default)
    {
        var binding = await _db.Bindings
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bindingId, ct)
            .ConfigureAwait(false);
        return binding is null ? null : ToDto(binding);
    }

    public async Task<IReadOnlyList<BindingDto>> ListByPolicyVersionAsync(
        Guid policyVersionId,
        bool includeDeleted,
        CancellationToken ct = default)
    {
        var query = _db.Bindings
            .AsNoTracking()
            .Where(b => b.PolicyVersionId == policyVersionId);
        if (!includeDeleted)
        {
            query = query.Where(b => b.DeletedAt == null);
        }
        var rows = await query
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<BindingDto>> ListByTargetAsync(
        BindingTargetType targetType,
        string targetRef,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetRef);

        // Exact-equality match — consumer resolution semantics in P3.4
        // (resolve) and P4 (hierarchy walk) require byte-exact (TargetType,
        // TargetRef) lookups. No prefix / case-folding here.
        var rows = await _db.Bindings
            .AsNoTracking()
            .Where(b => b.TargetType == targetType
                        && b.TargetRef == targetRef
                        && b.DeletedAt == null)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    private static BindingDto ToDto(Binding b) => new(
        b.Id,
        b.PolicyVersionId,
        b.TargetType,
        b.TargetRef,
        b.BindStrength,
        b.CreatedAt,
        b.CreatedBySubjectId,
        b.DeletedAt,
        b.DeletedBySubjectId);
}
