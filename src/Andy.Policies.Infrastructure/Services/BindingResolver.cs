// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Exact-match resolver for the <c>GET /api/bindings/resolve</c> endpoint
/// (P3.4, story rivoli-ai/andy-policies#22). Joins the live binding rows
/// to <c>PolicyVersion</c> and <c>Policy</c>, filters Retired versions,
/// dedups same-target/same-version pairs by preferring <c>Mandatory</c>
/// over <c>Recommended</c>, and emits a deterministic ordering (policy
/// name ASC, then version number DESC).
/// </summary>
public sealed class BindingResolver : IBindingResolver
{
    private readonly AppDbContext _db;

    public BindingResolver(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ResolveBindingsResponse> ResolveExactAsync(
        BindingTargetType targetType,
        string targetRef,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetRef);

        // Pull the rows we need with a single JOIN and let the in-memory
        // dedup + ordering happen client-side. Result sets are bounded by
        // the binding count for one target — handfuls in practice — so the
        // hot path is the indexed (TargetType, TargetRef) lookup.
        // Client-side ordering also keeps the query SQLite-safe (the
        // provider can't ORDER BY DateTimeOffset; see the BindingService
        // notes from P3.3).
        var query =
            from b in _db.Bindings.AsNoTracking()
            where b.TargetType == targetType
                  && b.TargetRef == targetRef
                  && b.DeletedAt == null
            join v in _db.PolicyVersions.AsNoTracking() on b.PolicyVersionId equals v.Id
            where v.State != LifecycleState.Retired
            join p in _db.Policies.AsNoTracking() on v.PolicyId equals p.Id
            select new
            {
                BindingId = b.Id,
                b.CreatedAt,
                BindStrength = b.BindStrength,
                PolicyId = p.Id,
                p.Name,
                PolicyVersionId = v.Id,
                v.Version,
                v.State,
                v.Enforcement,
                v.Severity,
                v.Scopes,
            };

        var rows = await query.ToListAsync(ct).ConfigureAwait(false);

        // Dedup by PolicyVersionId: Mandatory wins over Recommended; tiebreak
        // earliest CreatedAt. The administrative duplicate this guards
        // against ("oops, two bindings for the same target and version")
        // would otherwise show up to consumers as redundant rows.
        var deduped = rows
            .GroupBy(r => r.PolicyVersionId)
            .Select(g => g
                .OrderBy(r => r.BindStrength)         // Mandatory=1 < Recommended=2
                .ThenBy(r => r.CreatedAt)
                .First())
            .ToList();

        // Deterministic ordering for callers that snapshot the response.
        var ordered = deduped
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ThenByDescending(r => r.Version)
            .Select(r => new ResolvedBindingDto(
                BindingId: r.BindingId,
                PolicyId: r.PolicyId,
                PolicyName: r.Name,
                PolicyVersionId: r.PolicyVersionId,
                VersionNumber: r.Version,
                VersionState: r.State.ToString(),
                Enforcement: ToEnforcementWire(r.Enforcement),
                Severity: ToSeverityWire(r.Severity),
                Scopes: r.Scopes.ToArray(),
                BindStrength: r.BindStrength))
            .ToList();

        return new ResolveBindingsResponse(targetType, targetRef, ordered, ordered.Count);
    }

    private static string ToEnforcementWire(EnforcementLevel level) => level switch
    {
        EnforcementLevel.May => "MAY",
        EnforcementLevel.Should => "SHOULD",
        EnforcementLevel.Must => "MUST",
        _ => throw new InvalidOperationException($"Unknown EnforcementLevel: {level}"),
    };

    private static string ToSeverityWire(Severity severity) => severity switch
    {
        Severity.Info => "info",
        Severity.Moderate => "moderate",
        Severity.Critical => "critical",
        _ => throw new InvalidOperationException($"Unknown Severity: {severity}"),
    };
}
