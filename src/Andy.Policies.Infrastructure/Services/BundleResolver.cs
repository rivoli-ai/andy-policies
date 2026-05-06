// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Domain.ValueObjects;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// In-memory resolver over <c>Bundle.SnapshotJson</c> (P8.3, story
/// rivoli-ai/andy-policies#83). Reads the bundle row, deserialises the
/// snapshot once per <c>(bundleId, snapshotHash)</c> into an
/// <see cref="IMemoryCache"/> entry, and serves resolution + pinned-
/// policy lookups out of the cached <see cref="BundleSnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cache identity.</b> The cache key is
/// <c>(bundleId, snapshotHash)</c>. Bundles are immutable post-insert
/// (P8.1's SaveChanges immutability sweep), so a stale entry is
/// impossible: any change to the row implies a new <c>snapshotHash</c>
/// which produces a different key. Cache TTL is 5 minutes — a soft
/// upper bound to keep memory usage bounded under unbounded bundle
/// counts; on miss we re-deserialise from the row.
/// </para>
/// <para>
/// <b>Soft-delete posture.</b> Bundles in
/// <see cref="BundleState.Deleted"/> are excluded from resolution —
/// callers see a 404 from the controller. The bundle row remains in
/// the table for audit-chain integrity, but is not addressable from
/// the resolution surface.
/// </para>
/// </remarks>
public sealed class BundleResolver : IBundleResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public BundleResolver(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<BundleResolveResult?> ResolveAsync(
        Guid bundleId,
        BindingTargetType targetType,
        string targetRef,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetRef);

        var carrier = await LoadAsync(bundleId, ct).ConfigureAwait(false);
        if (carrier is null)
        {
            return null;
        }

        var targetTypeWire = targetType.ToString();
        var rows = carrier.Snapshot.Bindings
            .Where(b => string.Equals(b.TargetType, targetTypeWire, StringComparison.Ordinal)
                     && string.Equals(b.TargetRef, targetRef, StringComparison.Ordinal))
            .ToList();

        // Dedup by PolicyVersionId — Mandatory beats Recommended.
        // (Same rule as the live BindingResolver from P3.4. Snapshots
        // are pre-deduped at create time but the rule is cheap to
        // re-apply and defends against a future migration that
        // adds rows.)
        var policyById = carrier.Snapshot.Policies.ToDictionary(p => p.PolicyVersionId);
        var deduped = rows
            .GroupBy(r => r.PolicyVersionId)
            .Select(g => g
                .OrderBy(r => ParseStrength(r.BindStrength))
                .First())
            .ToList();

        var ordered = deduped
            .Select(b =>
            {
                policyById.TryGetValue(b.PolicyVersionId, out var policy);
                return new BundleResolvedBindingDto(
                    BindingId: b.BindingId,
                    PolicyId: policy?.PolicyId ?? Guid.Empty,
                    PolicyName: policy?.Name ?? string.Empty,
                    PolicyVersionId: b.PolicyVersionId,
                    VersionNumber: policy?.Version ?? 0,
                    Enforcement: ToEnforcementWire(policy?.Enforcement),
                    Severity: ToSeverityWire(policy?.Severity),
                    Scopes: policy?.Scopes ?? Array.Empty<string>(),
                    BindStrength: ParseStrength(b.BindStrength));
            })
            .OrderBy(d => d.PolicyName, StringComparer.Ordinal)
            .ThenByDescending(d => d.VersionNumber)
            .ToList();

        return new BundleResolveResult(
            BundleId: carrier.Id,
            BundleName: carrier.Name,
            SnapshotHash: carrier.SnapshotHash,
            CapturedAt: carrier.Snapshot.CapturedAt,
            TargetType: targetType,
            TargetRef: targetRef,
            Bindings: ordered,
            Count: ordered.Count);
    }

    public async Task<BundleSnapshotView?> GetSnapshotAsync(
        Guid bundleId, CancellationToken ct = default)
    {
        var carrier = await LoadAsync(bundleId, ct).ConfigureAwait(false);
        return carrier is null
            ? null
            : new BundleSnapshotView(carrier.Id, carrier.Name, carrier.SnapshotHash, carrier.Snapshot);
    }

    public async Task<EffectivePolicySetDto?> ResolveEffectiveForScopeAsync(
        Guid bundleId, Guid scopeNodeId, CancellationToken ct = default)
    {
        var carrier = await LoadAsync(bundleId, ct).ConfigureAwait(false);
        if (carrier is null) return null;

        // Walk the scope chain via ParentId from the snapshot. The
        // snapshot doesn't carry materialised paths, so we hand-walk;
        // depth defaults to chain index (0 at the root).
        var scopeById = carrier.Snapshot.Scopes.ToDictionary(s => s.ScopeNodeId);
        if (!scopeById.ContainsKey(scopeNodeId))
        {
            // Bundle exists but the scope node is not in the snapshot.
            // Mirror the live empty-set semantics rather than 404 — the
            // caller addressed a real bundle, just at a node that
            // doesn't appear there.
            return new EffectivePolicySetDto(
                ScopeNodeId: scopeNodeId,
                Policies: Array.Empty<EffectivePolicyDto>());
        }

        var chain = WalkAncestorChain(scopeById, scopeNodeId);
        var policiesByVersionId = carrier.Snapshot.Policies.ToDictionary(p => p.PolicyVersionId);

        // Match bindings that target ScopeNode and reference a chain
        // node via "scope:{nodeId}". Each candidate is tagged with its
        // node's depth (its position in the root→leaf chain) so the
        // tighten-only fold can prefer deeper Mandatory rows.
        var depthByNodeId = chain
            .Select((n, idx) => (n.ScopeNodeId, Depth: idx))
            .ToDictionary(p => p.ScopeNodeId, p => p.Depth);
        var candidates = new List<EffectiveCandidate>();
        foreach (var b in carrier.Snapshot.Bindings)
        {
            if (!string.Equals(b.TargetType, BindingTargetType.ScopeNode.ToString(), StringComparison.Ordinal))
            {
                continue;
            }
            if (!b.TargetRef.StartsWith("scope:", StringComparison.Ordinal)
                || !Guid.TryParse(b.TargetRef.AsSpan("scope:".Length), out var nodeId))
            {
                continue;
            }
            if (!depthByNodeId.TryGetValue(nodeId, out var depth)) continue;
            if (!policiesByVersionId.TryGetValue(b.PolicyVersionId, out var policy)) continue;
            var node = scopeById[nodeId];
            candidates.Add(new EffectiveCandidate(
                b, policy, depth, nodeId, ParseScopeType(node.Type)));
        }

        var folded = candidates
            .GroupBy(c => c.Policy.PolicyId)
            .Select(group =>
            {
                var mandatory = group
                    .Where(c => ParseStrength(c.Binding.BindStrength) == BindStrength.Mandatory)
                    .ToList();
                var winner = (mandatory.Count > 0 ? mandatory : group.ToList())
                    .OrderByDescending(c => c.Depth)
                    .First();
                return new EffectivePolicyDto(
                    PolicyId: winner.Policy.PolicyId,
                    PolicyVersionId: winner.Policy.PolicyVersionId,
                    PolicyKey: winner.Policy.Name,
                    Version: winner.Policy.Version,
                    BindStrength: ParseStrength(winner.Binding.BindStrength),
                    SourceBindingId: winner.Binding.BindingId,
                    SourceScopeNodeId: winner.ScopeNodeId,
                    SourceScopeType: winner.ScopeType,
                    SourceDepth: winner.Depth);
            })
            .OrderBy(p => p.BindStrength)
            .ThenBy(p => p.PolicyKey, StringComparer.Ordinal)
            .ToList();

        return new EffectivePolicySetDto(scopeNodeId, folded);
    }

    /// <summary>Root-to-leaf chain of scope nodes. The leaf is at the
    /// end of the list; depth increases with index. Stops at the
    /// first ancestor that is not in <paramref name="scopeById"/> —
    /// snapshots can be partial (the builder includes every node the
    /// catalog had at capture time, but a corrupted snapshot or a
    /// future schema-tightening could drop ancestors).</summary>
    private static List<BundleScopeEntry> WalkAncestorChain(
        IReadOnlyDictionary<Guid, BundleScopeEntry> scopeById, Guid leafId)
    {
        var visited = new HashSet<Guid>();
        var leafToRoot = new List<BundleScopeEntry>();
        var cursor = scopeById[leafId];
        while (true)
        {
            if (!visited.Add(cursor.ScopeNodeId)) break; // defensive: cycle
            leafToRoot.Add(cursor);
            if (cursor.ParentId is null) break;
            if (!scopeById.TryGetValue(cursor.ParentId.Value, out var parent)) break;
            cursor = parent;
        }
        leafToRoot.Reverse();
        return leafToRoot;
    }

    private static ScopeType ParseScopeType(string wire)
        => Enum.TryParse<ScopeType>(wire, ignoreCase: true, out var st) ? st : default;

    private sealed record EffectiveCandidate(
        BundleBindingEntry Binding,
        BundlePolicyEntry Policy,
        int Depth,
        Guid ScopeNodeId,
        ScopeType ScopeType);

    public async Task<BundlePinnedPolicyDto?> GetPinnedPolicyAsync(
        Guid bundleId,
        Guid policyId,
        CancellationToken ct = default)
    {
        var carrier = await LoadAsync(bundleId, ct).ConfigureAwait(false);
        if (carrier is null)
        {
            return null;
        }

        var policy = carrier.Snapshot.Policies.FirstOrDefault(p => p.PolicyId == policyId);
        if (policy is null)
        {
            return null;
        }

        return new BundlePinnedPolicyDto(
            BundleId: carrier.Id,
            BundleName: carrier.Name,
            SnapshotHash: carrier.SnapshotHash,
            CapturedAt: carrier.Snapshot.CapturedAt,
            PolicyId: policy.PolicyId,
            PolicyName: policy.Name,
            PolicyVersionId: policy.PolicyVersionId,
            VersionNumber: policy.Version,
            Enforcement: ToEnforcementWire(policy.Enforcement),
            Severity: ToSeverityWire(policy.Severity),
            Scopes: policy.Scopes,
            RulesJson: policy.RulesJson,
            Summary: policy.Summary);
    }

    /// <summary>
    /// Load the bundle row + parsed snapshot. Returns <c>null</c>
    /// when the bundle is missing or soft-deleted. The parsed
    /// snapshot is cached keyed by <c>(bundleId, snapshotHash)</c>
    /// so two requests against the same bundle pay the JSON parse
    /// cost only once within the TTL.
    /// </summary>
    private async Task<SnapshotCarrier?> LoadAsync(Guid bundleId, CancellationToken ct)
    {
        var head = await _db.Bundles
            .AsNoTracking()
            .Where(b => b.Id == bundleId && b.State == BundleState.Active)
            .Select(b => new { b.Id, b.Name, b.SnapshotHash, b.SnapshotJson })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (head is null) return null;

        var cacheKey = ($"bundle-snapshot::{head.Id:N}::{head.SnapshotHash}", typeof(SnapshotCarrier));
        if (_cache.TryGetValue(cacheKey, out SnapshotCarrier? cached) && cached is not null)
        {
            return cached;
        }

        var snapshot = JsonSerializer.Deserialize<BundleSnapshot>(head.SnapshotJson, SnapshotJsonOptions)
            ?? throw new InvalidOperationException(
                $"Bundle {bundleId} has a SnapshotJson that did not deserialise into a BundleSnapshot.");

        var carrier = new SnapshotCarrier(head.Id, head.Name, head.SnapshotHash, snapshot);
        _cache.Set(cacheKey, carrier, CacheTtl);
        return carrier;
    }

    private static BindStrength ParseStrength(string wire)
        => Enum.TryParse<BindStrength>(wire, ignoreCase: true, out var bs)
            ? bs
            : BindStrength.Recommended;

    private static string ToEnforcementWire(string? snapshotValue)
    {
        // Snapshot stores Enforcement.ToString() — i.e. "May", "Should", "Must".
        // Wire convention from ADR 0001 §6 is the upper-case RFC 2119 token.
        if (string.IsNullOrEmpty(snapshotValue)) return string.Empty;
        return snapshotValue.ToUpperInvariant();
    }

    private static string ToSeverityWire(string? snapshotValue)
    {
        // Snapshot stores Severity.ToString() — "Info", "Moderate", "Critical".
        // Wire convention is lowercase.
        if (string.IsNullOrEmpty(snapshotValue)) return string.Empty;
        return snapshotValue.ToLowerInvariant();
    }

    private sealed record SnapshotCarrier(
        Guid Id, string Name, string SnapshotHash, BundleSnapshot Snapshot);
}
