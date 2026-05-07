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
    private readonly ITightenOnlyValidator? _tightenValidator;

    public BindingService(AppDbContext db, IAuditWriter audit, TimeProvider clock)
        : this(db, audit, clock, tightenValidator: null) { }

    /// <summary>
    /// Optional <see cref="ITightenOnlyValidator"/> overload (P4.4,
    /// rivoli-ai/andy-policies#32). Production DI passes the live
    /// validator so <see cref="CreateAsync"/> rejects mutations that
    /// would loosen a Mandatory binding declared upstream. Existing
    /// unit tests that construct <c>BindingService</c> directly fall
    /// through the parameterless overload above and see the legacy
    /// behaviour (no tighten check), preserving the P3 test
    /// inventory unchanged.
    /// </summary>
    public BindingService(
        AppDbContext db,
        IAuditWriter audit,
        TimeProvider clock,
        ITightenOnlyValidator? tightenValidator)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
        _tightenValidator = tightenValidator;
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

        // P4.4: stricter-tightens-only — reject Recommended creates that
        // would shadow a Mandatory binding declared upstream. The
        // validator returns null when no scope chain is involved (soft
        // refs) or when the proposal is itself Mandatory; we throw on
        // every non-null violation.
        if (_tightenValidator is not null)
        {
            var violation = await _tightenValidator.ValidateCreateAsync(
                version.Id, request.TargetType, targetRef, request.BindStrength, ct)
                .ConfigureAwait(false);
            if (violation is not null)
            {
                throw new TightenOnlyViolationException(violation);
            }
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
            "binding.created", binding.Id, actorSubjectId, request.Rationale, ct)
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
        // SQLite does not support ORDER BY on DateTimeOffset (the binary
        // form is opaque to the provider's collation), so we materialise
        // and sort client-side. Result sets are bounded by the binding
        // count for a single version (small in practice — a handful per
        // version), so the cost is negligible compared to the round-trip.
        var rows = await query
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows
            .OrderByDescending(b => b.CreatedAt)
            .Select(ToDto)
            .ToList();
    }

    public async Task<IReadOnlyList<BindingDto>> ListByTargetAsync(
        BindingTargetType targetType,
        string targetRef,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetRef);

        // Exact-equality match — consumer resolution semantics in P3.4
        // (resolve) and P4 (hierarchy walk) require byte-exact (TargetType,
        // TargetRef) lookups. No prefix / case-folding here. Client-side
        // sort for SQLite parity (see ListByPolicyVersionAsync above).
        var rows = await _db.Bindings
            .AsNoTracking()
            .Where(b => b.TargetType == targetType
                        && b.TargetRef == targetRef
                        && b.DeletedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows
            .OrderByDescending(b => b.CreatedAt)
            .Select(ToDto)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> SearchTargetRefsAsync(
        BindingTargetType targetType,
        string? search,
        int take,
        CancellationToken ct = default)
    {
        // P9 follow-up #198 (2026-05-07): distinct, alphabetically-ordered
        // TargetRef autocomplete source. Pre-filtering on TargetType +
        // DeletedAt is index-friendly (the per-type Bindings index covers
        // it); the prefix predicate is `LIKE :search%` server-side, which
        // EF translates to either a SARGable index seek (Postgres + SQLite
        // when the column collation supports it) or a scan with the same
        // result. `take` is clamped to a sane upper bound; values <= 0
        // collapse to the default page size.
        var clamped = take > 0 && take <= 100 ? take : 20;
        var prefix = string.IsNullOrEmpty(search) ? null : search;

        var query = _db.Bindings
            .AsNoTracking()
            .Where(b => b.TargetType == targetType && b.DeletedAt == null);
        if (prefix is not null)
        {
            // EF.Functions.Like covers Postgres + SQLite uniformly; the
            // escape-then-wildcard is the most portable shape.
            query = query.Where(b => EF.Functions.Like(b.TargetRef, prefix + "%"));
        }
        var rows = await query
            .Select(b => b.TargetRef)
            .Distinct()
            .OrderBy(r => r)
            .Take(clamped)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows;
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
