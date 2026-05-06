// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.Queries;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Domain.ValueObjects;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Snapshot-backed implementation of <see cref="IBundleBackedPolicyReader"/>
/// (P8.4, rivoli-ai/andy-policies#84). Delegates to
/// <see cref="IBundleResolver.GetSnapshotAsync"/> for the cached
/// parsed <see cref="BundleSnapshot"/>, then projects entries into
/// the existing wire DTOs with snapshot-derived surrogates for
/// fields the snapshot does not carry.
/// </summary>
public sealed class BundleBackedPolicyReader : IBundleBackedPolicyReader
{
    /// <summary>Sentinel value used for subject-id columns the
    /// snapshot does not carry. Distinct from any real Andy Auth
    /// subject id so consumers cannot accidentally mistake it for
    /// an actor.</summary>
    public const string SnapshotSubjectIdSentinel = "snapshot";

    /// <summary>Hard upper bound on take, mirrors
    /// <c>PolicyService.MaxPageSize</c>.</summary>
    private const int MaxPageSize = 500;

    private readonly IBundleResolver _resolver;

    public BundleBackedPolicyReader(IBundleResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task<IReadOnlyList<PolicyDto>?> ListPoliciesAsync(
        Guid bundleId, ListPoliciesQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var view = await _resolver.GetSnapshotAsync(bundleId, ct).ConfigureAwait(false);
        if (view is null) return null;

        IEnumerable<BundlePolicyEntry> rows = view.Snapshot.Policies;

        if (!string.IsNullOrEmpty(query.NamePrefix))
        {
            rows = rows.Where(p => p.Name.StartsWith(query.NamePrefix, StringComparison.Ordinal));
        }
        if (!string.IsNullOrEmpty(query.Scope))
        {
            rows = rows.Where(p => p.Scopes.Contains(query.Scope, StringComparer.Ordinal));
        }
        if (!string.IsNullOrEmpty(query.Enforcement))
        {
            // Snapshot stores Enforcement.ToString() (e.g. "Should");
            // query passes either case-insensitive enum name or wire
            // form (e.g. "SHOULD"). Compare via parsed enum so both
            // shapes work.
            if (Enum.TryParse<EnforcementLevel>(query.Enforcement, ignoreCase: true, out var parsed))
            {
                rows = rows.Where(p => string.Equals(
                    p.Enforcement, parsed.ToString(), StringComparison.Ordinal));
            }
            else
            {
                rows = Enumerable.Empty<BundlePolicyEntry>();
            }
        }
        if (!string.IsNullOrEmpty(query.Severity))
        {
            if (Enum.TryParse<Severity>(query.Severity, ignoreCase: true, out var parsed))
            {
                rows = rows.Where(p => string.Equals(
                    p.Severity, parsed.ToString(), StringComparison.Ordinal));
            }
            else
            {
                rows = Enumerable.Empty<BundlePolicyEntry>();
            }
        }

        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, MaxPageSize);

        // Distinct by PolicyId — the snapshot carries one Active
        // version per policy by construction (P8.2 builder enforces
        // it via the Active filter), but defending against a future
        // schema change keeps the DTO contract right.
        var distinct = rows
            .GroupBy(p => p.PolicyId)
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Skip(skip)
            .Take(take)
            .ToList();

        return distinct.Select(p => MapPolicy(view, p)).ToList();
    }

    public async Task<PolicyDto?> GetPolicyAsync(
        Guid bundleId, Guid policyId, CancellationToken ct = default)
    {
        var view = await _resolver.GetSnapshotAsync(bundleId, ct).ConfigureAwait(false);
        if (view is null) return null;
        var entry = view.Snapshot.Policies.FirstOrDefault(p => p.PolicyId == policyId);
        return entry is null ? null : MapPolicy(view, entry);
    }

    public async Task<PolicyDto?> GetPolicyByNameAsync(
        Guid bundleId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var view = await _resolver.GetSnapshotAsync(bundleId, ct).ConfigureAwait(false);
        if (view is null) return null;
        var entry = view.Snapshot.Policies.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.Ordinal));
        return entry is null ? null : MapPolicy(view, entry);
    }

    public async Task<IReadOnlyList<PolicyVersionDto>?> ListVersionsAsync(
        Guid bundleId, Guid policyId, CancellationToken ct = default)
    {
        var view = await _resolver.GetSnapshotAsync(bundleId, ct).ConfigureAwait(false);
        if (view is null) return null;
        var entries = view.Snapshot.Policies.Where(p => p.PolicyId == policyId).ToList();
        // The snapshot carries only the Active version per policy, so
        // ListVersions returns at most one row — by design. Documented
        // on IBundleBackedPolicyReader.
        return entries.Select(e => MapVersion(view, e)).ToList();
    }

    public async Task<PolicyVersionDto?> GetVersionAsync(
        Guid bundleId, Guid policyId, Guid versionId, CancellationToken ct = default)
    {
        var view = await _resolver.GetSnapshotAsync(bundleId, ct).ConfigureAwait(false);
        if (view is null) return null;
        var entry = view.Snapshot.Policies.FirstOrDefault(p =>
            p.PolicyId == policyId && p.PolicyVersionId == versionId);
        return entry is null ? null : MapVersion(view, entry);
    }

    public async Task<PolicyVersionDto?> GetActiveVersionAsync(
        Guid bundleId, Guid policyId, CancellationToken ct = default)
    {
        var view = await _resolver.GetSnapshotAsync(bundleId, ct).ConfigureAwait(false);
        if (view is null) return null;
        var entry = view.Snapshot.Policies.FirstOrDefault(p => p.PolicyId == policyId);
        return entry is null ? null : MapVersion(view, entry);
    }

    private static PolicyDto MapPolicy(BundleSnapshotView view, BundlePolicyEntry entry) => new(
        Id: entry.PolicyId,
        Name: entry.Name,
        Description: null,
        CreatedAt: view.Snapshot.CapturedAt,
        CreatedBySubjectId: SnapshotSubjectIdSentinel,
        VersionCount: 1,
        ActiveVersionId: entry.PolicyVersionId);

    private static PolicyVersionDto MapVersion(BundleSnapshotView view, BundlePolicyEntry entry) => new(
        Id: entry.PolicyVersionId,
        PolicyId: entry.PolicyId,
        Version: entry.Version,
        State: LifecycleState.Active.ToString(),
        Enforcement: ToEnforcementWire(entry.Enforcement),
        Severity: ToSeverityWire(entry.Severity),
        Scopes: entry.Scopes,
        Summary: entry.Summary,
        RulesJson: entry.RulesJson,
        CreatedAt: view.Snapshot.CapturedAt,
        CreatedBySubjectId: SnapshotSubjectIdSentinel,
        ProposerSubjectId: SnapshotSubjectIdSentinel);

    private static string ToEnforcementWire(string snapshotValue) =>
        string.IsNullOrEmpty(snapshotValue) ? string.Empty : snapshotValue.ToUpperInvariant();

    private static string ToSeverityWire(string snapshotValue) =>
        string.IsNullOrEmpty(snapshotValue) ? string.Empty : snapshotValue.ToLowerInvariant();
}
