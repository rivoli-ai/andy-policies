// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Domain.ValueObjects;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Reads the live catalog and produces a
/// <see cref="BundleSnapshot"/>. P8.2 (#82). Caller owns the
/// serializable transaction; the builder only enumerates.
/// </summary>
public sealed class BundleSnapshotBuilder : IBundleSnapshotBuilder
{
    private const string SchemaVersion = "1";

    /// <summary>32 zero hex chars (lowercase), used when the audit
    /// chain is empty. Matches the genesis prev-hash of the chain
    /// (P6.2 #42), so a bundle taken before any audit event still has
    /// a stable, well-defined audit-tail-hash field.</summary>
    private static readonly string EmptyChainTailHashHex = new('0', 64);

    private readonly AppDbContext _db;

    public BundleSnapshotBuilder(AppDbContext db)
    {
        _db = db;
    }

    public async Task<BundleSnapshot> BuildAsync(
        DateTimeOffset capturedAt,
        bool includeOverrides = true,
        CancellationToken ct = default)
    {
        // Active policy versions, ordered (PolicyId, Version) for
        // deterministic serialisation. Include the parent Policy so
        // the snapshot carries the policy name without a second
        // round-trip per row at read time.
        var policyRows = await _db.PolicyVersions
            .AsNoTracking()
            .Where(v => v.State == LifecycleState.Active)
            .OrderBy(v => v.PolicyId).ThenBy(v => v.Version)
            .Select(v => new
            {
                v.PolicyId,
                PolicyName = v.Policy!.Name,
                v.Id,
                v.Version,
                v.Enforcement,
                v.Severity,
                v.Scopes,
                v.RulesJson,
                v.Summary,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var policies = policyRows
            .Select(r => new BundlePolicyEntry(
                r.PolicyId,
                r.PolicyName,
                r.Id,
                r.Version,
                r.Enforcement.ToString(),
                r.Severity.ToString(),
                r.Scopes.ToList(),
                r.RulesJson,
                r.Summary))
            .ToList();

        var activePvIds = policies.Select(p => p.PolicyVersionId).ToHashSet();

        // Live bindings against an Active policy version. Soft-deleted
        // (DeletedAt non-null) rows and bindings to non-Active
        // versions are excluded — they would have no addressable
        // policy in the snapshot.
        var bindings = await _db.Bindings
            .AsNoTracking()
            .Where(b => b.DeletedAt == null && activePvIds.Contains(b.PolicyVersionId))
            .OrderBy(b => b.Id)
            .Select(b => new BundleBindingEntry(
                b.Id,
                b.PolicyVersionId,
                b.TargetType.ToString(),
                b.TargetRef,
                b.BindStrength.ToString()))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Approved overrides whose ExpiresAt is strictly after
        // capturedAt. Proposed / Revoked / Expired rows have no
        // runtime effect; including them would mislead consumers.
        // SQLite cannot translate DateTimeOffset comparisons (same
        // posture as the OverrideExpiryReaper from P5.3 — see comment
        // there), so we filter on State server-side (covered by
        // ix_overrides_scope_state and ix_overrides_expiry_approved)
        // and refine on ExpiresAt client-side. The Approved set is
        // bounded in practice; for catalogs that grow large here, a
        // future migration would push the predicate via raw SQL.
        //
        // P9 follow-up #205 (2026-05-07): when `includeOverrides` is
        // false, skip the query entirely and emit an empty list.
        // Compliance / immutability bundles use this to publish a
        // snapshot whose runtime behavior is governed strictly by the
        // active policy + binding set, with no override surface.
        List<BundleOverrideEntry> overrides;
        if (includeOverrides)
        {
            var approvedRows = await _db.Overrides
                .AsNoTracking()
                .Where(o => o.State == OverrideState.Approved)
                .Select(o => new
                {
                    o.Id,
                    o.PolicyVersionId,
                    o.ScopeKind,
                    o.ScopeRef,
                    o.Effect,
                    o.ReplacementPolicyVersionId,
                    o.ExpiresAt,
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            overrides = approvedRows
                .Where(o => o.ExpiresAt > capturedAt)
                .OrderBy(o => o.Id)
                .Select(o => new BundleOverrideEntry(
                    o.Id,
                    o.PolicyVersionId,
                    o.ScopeKind.ToString(),
                    o.ScopeRef,
                    o.Effect.ToString(),
                    o.ReplacementPolicyVersionId,
                    o.ExpiresAt))
                .ToList();
        }
        else
        {
            overrides = new List<BundleOverrideEntry>();
        }

        // Scope tree: every node, ordered by Id. Consumers reconstruct
        // the hierarchy via ParentId; the snapshot does not pre-build
        // the tree because Bundle pinning is per-row, not per-shape.
        var scopes = await _db.ScopeNodes
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => new BundleScopeEntry(
                s.Id,
                s.ParentId,
                s.Type.ToString(),
                s.Ref,
                s.DisplayName))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var auditTailHashHex = await GetAuditTailHashHexAsync(ct).ConfigureAwait(false);

        return new BundleSnapshot(
            SchemaVersion: SchemaVersion,
            CapturedAt: capturedAt,
            AuditTailHash: auditTailHashHex,
            Policies: policies,
            Bindings: bindings,
            Overrides: overrides,
            Scopes: scopes);
    }

    /// <summary>
    /// Read the current audit chain tail hash. Returns 64 zero hex
    /// chars when the chain is empty (matches the chain's genesis
    /// prev-hash convention from P6.2). The hash is hex-encoded
    /// lower-case so it round-trips identically to the form used by
    /// <c>ChainVerificationResult</c> and the audit-export bundle.
    /// </summary>
    private async Task<string> GetAuditTailHashHexAsync(CancellationToken ct)
    {
        var tailHash = await _db.AuditEvents
            .AsNoTracking()
            .OrderByDescending(e => e.Seq)
            .Select(e => e.Hash)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return tailHash is null
            ? EmptyChainTailHashHex
            : Convert.ToHexString(tailHash).ToLowerInvariant();
    }
}
