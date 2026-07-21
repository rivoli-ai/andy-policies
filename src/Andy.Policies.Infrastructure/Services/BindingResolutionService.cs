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
/// Hierarchy-aware binding resolver (P4.3, story
/// rivoli-ai/andy-policies#30). Walks the scope chain from root Org
/// down to the leaf (or to the resolved target) and folds the binding
/// set with stricter-tightens-only semantics. The fold rule is the
/// header decision of Epic P4: a Mandatory binding anywhere in the
/// chain cannot be downgraded by a descendant; the deepest binding of
/// the strictest strength wins.
/// </summary>
public sealed class BindingResolutionService : IBindingResolutionService
{
    private readonly AppDbContext _db;
    private readonly IScopeService _scopes;
    private readonly IOverrideService? _overrides;

    public BindingResolutionService(
        AppDbContext db,
        IScopeService scopes,
        IOverrideService? overrides = null)
    {
        _db = db;
        _scopes = scopes;
        _overrides = overrides;
    }

    public async Task<EffectivePolicySetDto> ResolveForScopeAsync(
        Guid scopeNodeId,
        CancellationToken ct = default)
        => await ResolveForScopeCoreAsync(scopeNodeId, null, ct).ConfigureAwait(false);

    public async Task<EffectivePolicySetDto> ResolveForScopeAsync(
        Guid scopeNodeId,
        OverrideResolutionContext overrideContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(overrideContext);
        return await ResolveForScopeCoreAsync(scopeNodeId, overrideContext, ct).ConfigureAwait(false);
    }

    private async Task<EffectivePolicySetDto> ResolveForScopeCoreAsync(
        Guid scopeNodeId,
        OverrideResolutionContext? overrideContext,
        CancellationToken ct)
    {
        var leaf = await _db.ScopeNodes.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == scopeNodeId, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"ScopeNode {scopeNodeId} not found.");

        var ancestors = await _scopes.GetAncestorsAsync(scopeNodeId, ct).ConfigureAwait(false);
        var chain = ancestors.Select(a => new ChainNode(a.Id, a.Type, a.Ref, a.Depth)).ToList();
        chain.Add(new ChainNode(leaf.Id, leaf.Type, leaf.Ref, leaf.Depth));

        var policies = await ResolveAlongChainAsync(chain, ct).ConfigureAwait(false);
        IReadOnlyList<AppliedOverrideDto> appliedOverrides = Array.Empty<AppliedOverrideDto>();
        if (overrideContext is not null && _overrides is not null)
        {
            var applied = await ApplyOverridesAsync(policies, overrideContext, ct).ConfigureAwait(false);
            policies = applied.Policies;
            appliedOverrides = applied.AppliedOverrides;
        }
        return new EffectivePolicySetDto(scopeNodeId, policies)
        {
            AppliedOverrides = appliedOverrides,
        };
    }

    private async Task<(IReadOnlyList<EffectivePolicyDto> Policies,
        IReadOnlyList<AppliedOverrideDto> AppliedOverrides)> ApplyOverridesAsync(
        IReadOnlyList<EffectivePolicyDto> policies,
        OverrideResolutionContext context,
        CancellationToken ct)
    {
        var principal = string.IsNullOrWhiteSpace(context.PrincipalSubjectId)
            ? Array.Empty<OverrideDto>()
            : (await _overrides!.GetActiveAsync(
                OverrideScopeKind.Principal,
                context.PrincipalSubjectId.Trim(),
                ct).ConfigureAwait(false)).ToArray();

        var cohorts = new List<OverrideDto>();
        foreach (var cohort in context.CohortRefs
                     .Where(c => !string.IsNullOrWhiteSpace(c))
                     .Select(c => c.Trim())
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(c => c, StringComparer.Ordinal))
        {
            cohorts.AddRange(await _overrides!.GetActiveAsync(
                OverrideScopeKind.Cohort, cohort, ct).ConfigureAwait(false));
        }

        var result = new List<EffectivePolicyDto>(policies.Count);
        var appliedOverrides = new List<AppliedOverrideDto>();
        foreach (var policy in policies)
        {
            // Principal-specific grants are more specific than cohort
            // grants. Within one specificity, latest approval wins.
            var match = principal
                .Where(o => o.PolicyVersionId == policy.PolicyVersionId)
                .OrderBy(o => o.ApprovedAt)
                .ThenBy(o => o.Id)
                .LastOrDefault()
                ?? cohorts
                    .Where(o => o.PolicyVersionId == policy.PolicyVersionId)
                    .OrderBy(o => o.ApprovedAt)
                    .ThenBy(o => o.ScopeRef, StringComparer.Ordinal)
                    .ThenBy(o => o.Id)
                    .LastOrDefault();

            if (match is null)
            {
                result.Add(policy);
                continue;
            }
            if (match.Effect == OverrideEffect.Exempt)
            {
                appliedOverrides.Add(ToAppliedOverride(match));
                continue;
            }
            if (match.ReplacementPolicyVersionId is not { } replacementId)
            {
                result.Add(policy);
                continue;
            }

            var replacement = await _db.PolicyVersions.AsNoTracking()
                .Where(v => v.Id == replacementId && v.State != LifecycleState.Retired)
                .Select(v => new
                {
                    Version = v,
                    PolicyId = v.PolicyId,
                    PolicyName = v.Policy!.Name,
                })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (replacement is null)
            {
                // A stale replacement cannot relax the baseline by
                // making it disappear; retain the original policy.
                result.Add(policy);
                continue;
            }

            result.Add(policy with
            {
                PolicyId = replacement.PolicyId,
                PolicyVersionId = replacement.Version.Id,
                PolicyKey = replacement.PolicyName,
                Version = replacement.Version.Version,
            });
            appliedOverrides.Add(ToAppliedOverride(match));
        }

        var effective = result
            .GroupBy(p => p.PolicyId)
            .Select(g => g.OrderBy(p => p.BindStrength).ThenByDescending(p => p.SourceDepth).First())
            .OrderBy(p => p.BindStrength)
            .ThenBy(p => p.PolicyKey, StringComparer.Ordinal)
            .ToList();
        return (effective, appliedOverrides
            .OrderBy(o => o.OriginalPolicyVersionId)
            .ThenBy(o => o.OverrideId)
            .ToList());
    }

    private static AppliedOverrideDto ToAppliedOverride(OverrideDto value) => new(
        value.Id,
        value.Effect,
        value.ScopeKind,
        value.ScopeRef,
        value.PolicyVersionId,
        value.ReplacementPolicyVersionId);

    public async Task<EffectivePolicySetDto> ResolveForTargetAsync(
        BindingTargetType targetType,
        string targetRef,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetRef);

        // Try the bridge: BindingTargetType maps to ScopeType for the
        // overlapping levels (Org/Tenant/Repo/Template). Team and Run
        // have no BindingTargetType; ScopeNode is the explicit
        // chain-walk path. If we find a ScopeNode by (Type, Ref),
        // hand off to the chain walker.
        if (TryMapToScopeType(targetType, out var scopeType))
        {
            var match = await _db.ScopeNodes.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Type == scopeType && s.Ref == targetRef, ct)
                .ConfigureAwait(false);
            if (match is not null)
            {
                return await ResolveForScopeAsync(match.Id, ct).ConfigureAwait(false);
            }
        }
        else if (targetType == BindingTargetType.ScopeNode)
        {
            // Caller passed scopeRef as a ScopeNode target — let them
            // address the node directly. The conventional shape is
            // "scope:{guid}" but we accept either form.
            if (Guid.TryParse(targetRef, out var directId)
                || (targetRef.StartsWith("scope:", StringComparison.Ordinal)
                    && Guid.TryParse(targetRef.AsSpan("scope:".Length), out directId)))
            {
                var directMatch = await _db.ScopeNodes.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == directId, ct)
                    .ConfigureAwait(false);
                if (directMatch is not null)
                {
                    return await ResolveForScopeAsync(directMatch.Id, ct).ConfigureAwait(false);
                }
            }
        }

        // Fallback to P3 exact-match: no scope node, just whatever
        // bindings target this exact pair. SourceScopeNodeId stays
        // null on the envelope so callers can tell the difference.
        var (fallbackRows, fallbackVersions, fallbackPolicies) = await LoadBindingsWithHydrationAsync(
                _db.Bindings.AsNoTracking()
                    .Where(b => b.TargetType == targetType && b.TargetRef == targetRef && b.DeletedAt == null),
                ct)
            .ConfigureAwait(false);
        var policies = FoldRows(fallbackRows, scopeDepthLookup: null,
            versionById: fallbackVersions, policyById: fallbackPolicies);
        return new EffectivePolicySetDto(ScopeNodeId: null, policies);
    }

    private async Task<IReadOnlyList<EffectivePolicyDto>> ResolveAlongChainAsync(
        IReadOnlyList<ChainNode> chain,
        CancellationToken ct)
    {
        // Build the predicate set for one round-trip. For each node we
        // pick up:
        //   1. ScopeNode-targeted bindings via "scope:{nodeId}".
        //   2. Bridge bindings — the same row a P3 caller would create
        //      against the leaf's external ref (e.g. "repo:org/name"
        //      with TargetType=Repo). This lets the catalog stay
        //      backward-compatible with bindings authored before
        //      scope nodes existed.
        var scopeNodeRefs = chain.Select(c => $"scope:{c.Id}").ToList();
        var bridgeKeys = chain
            .Select(c => (Type: TryMapToBindingTargetType(c.Type), Ref: c.Ref))
            .Where(p => p.Type is not null)
            .Select(p => new BridgeKey(p.Type!.Value, p.Ref))
            .Distinct()
            .ToList();

        // EF Core 8 can't combine the two filters into one query
        // directly, but we can pull each subset and merge in memory —
        // both subsets are bounded by the chain depth (≤ 6) × bindings
        // per node, so the cost is small and avoids a recursive CTE.
        var scopeBound = await _db.Bindings.AsNoTracking()
            .Where(b => b.TargetType == BindingTargetType.ScopeNode
                        && b.DeletedAt == null
                        && scopeNodeRefs.Contains(b.TargetRef))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var bridgeBound = new List<Binding>();
        if (bridgeKeys.Count > 0)
        {
            var bridgeTypes = bridgeKeys.Select(k => k.Type).Distinct().ToList();
            var bridgeRefs = bridgeKeys.Select(k => k.Ref).Distinct().ToList();
            var candidates = await _db.Bindings.AsNoTracking()
                .Where(b => bridgeTypes.Contains(b.TargetType)
                            && bridgeRefs.Contains(b.TargetRef)
                            && b.DeletedAt == null)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            // Filter to the exact (Type, Ref) pairs we care about; the
            // pre-filter above is wider to keep the SQL simple.
            var keySet = new HashSet<BridgeKey>(bridgeKeys);
            bridgeBound = candidates
                .Where(b => keySet.Contains(new BridgeKey(b.TargetType, b.TargetRef)))
                .ToList();
        }

        // Hydrate PolicyVersion + Policy in one shot.
        var versionIds = scopeBound.Concat(bridgeBound).Select(b => b.PolicyVersionId).Distinct().ToList();
        var versions = await _db.PolicyVersions.AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var policyIds = versions.Select(v => v.PolicyId).Distinct().ToList();
        var policies = await _db.Policies.AsNoTracking()
            .Where(p => policyIds.Contains(p.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var versionById = versions.ToDictionary(v => v.Id);
        var policyById = policies.ToDictionary(p => p.Id);

        // Build a lookup from each binding to the depth of the scope
        // node it semantically attached to. For ScopeNode-targeted
        // bindings, parse the targetRef tail. For bridge-bound, look
        // up by (Type, Ref). When a row touches multiple chain nodes
        // (shouldn't happen with our keys, but be safe), pick the
        // deepest occurrence.
        var depthByBinding = new Dictionary<Guid, (int Depth, Guid? ScopeNodeId, ScopeType? ScopeType)>();
        var chainScopeRefById = chain.ToDictionary(c => c.Id);
        foreach (var b in scopeBound)
        {
            // "scope:{guid}" → guid → chain node depth
            if (b.TargetRef.StartsWith("scope:", StringComparison.Ordinal)
                && Guid.TryParse(b.TargetRef.AsSpan("scope:".Length), out var nodeId)
                && chainScopeRefById.TryGetValue(nodeId, out var node))
            {
                depthByBinding[b.Id] = (node.Depth, node.Id, node.Type);
            }
        }
        foreach (var b in bridgeBound)
        {
            // Find the chain node whose (Type, Ref) bridges to this
            // binding. Pick the deepest if multiple match (defensive).
            var matchingChain = chain
                .Where(c => TryMapToBindingTargetType(c.Type) == b.TargetType && c.Ref == b.TargetRef)
                .OrderByDescending(c => c.Depth)
                .FirstOrDefault();
            if (matchingChain is not null)
            {
                depthByBinding[b.Id] = (matchingChain.Depth, matchingChain.Id, matchingChain.Type);
            }
        }

        var allBindings = scopeBound.Concat(bridgeBound).Distinct(new BindingIdComparer()).ToList();
        return FoldRows(
            allBindings,
            scopeDepthLookup: bindingId => depthByBinding.TryGetValue(bindingId, out var info) ? info : default,
            versionById: versionById,
            policyById: policyById);
    }

    private static IReadOnlyList<EffectivePolicyDto> FoldRows(
        IReadOnlyList<Binding> bindings,
        Func<Guid, (int Depth, Guid? ScopeNodeId, ScopeType? ScopeType)>? scopeDepthLookup = null,
        Dictionary<Guid, PolicyVersion>? versionById = null,
        Dictionary<Guid, Policy>? policyById = null)
    {
        if (bindings.Count == 0)
        {
            return Array.Empty<EffectivePolicyDto>();
        }

        // Need the version + policy hydration; if the caller didn't
        // provide them, assume the bindings list is already small
        // enough to hydrate per-row (used by the fallback path).
        return bindings
            .Select(b =>
            {
                var depthInfo = scopeDepthLookup?.Invoke(b.Id) ?? default;
                versionById ??= new Dictionary<Guid, PolicyVersion>();
                policyById ??= new Dictionary<Guid, Policy>();
                if (!versionById.TryGetValue(b.PolicyVersionId, out var version)
                    || !policyById.TryGetValue(version.PolicyId, out var policy))
                {
                    return (EffectiveCandidate?)null;
                }
                return new EffectiveCandidate(
                    Binding: b,
                    Version: version,
                    Policy: policy,
                    Depth: depthInfo.Depth,
                    ScopeNodeId: depthInfo.ScopeNodeId,
                    ScopeType: depthInfo.ScopeType);
            })
            .Where(c => c is not null)
            .Select(c => c!)
            .GroupBy(c => c.Policy.Id)
            .Select(group =>
            {
                // Tighten-only: pick the deepest Mandatory if any;
                // otherwise the deepest Recommended. Tiebreak earliest
                // CreatedAt — older wins so ordering is deterministic.
                var mandatory = group.Where(c => c.Binding.BindStrength == BindStrength.Mandatory).ToList();
                var winner = (mandatory.Count > 0 ? mandatory : group.ToList())
                    .OrderByDescending(c => c.Depth)
                    .ThenByDescending(c => c.Binding.BindStrength)  // Mandatory(1) wins ties since we already filtered
                    .ThenBy(c => c.Binding.CreatedAt)
                    .First();
                return new EffectivePolicyDto(
                    PolicyId: winner.Policy.Id,
                    PolicyVersionId: winner.Version.Id,
                    PolicyKey: winner.Policy.Name,
                    Version: winner.Version.Version,
                    BindStrength: winner.Binding.BindStrength,
                    SourceBindingId: winner.Binding.Id,
                    SourceScopeNodeId: winner.ScopeNodeId,
                    SourceScopeType: winner.ScopeType,
                    SourceDepth: winner.Depth);
            })
            .OrderBy(p => p.BindStrength)  // Mandatory(1) before Recommended(2)
            .ThenBy(p => p.PolicyKey, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<(IReadOnlyList<Binding> Rows, Dictionary<Guid, PolicyVersion> Versions, Dictionary<Guid, Policy> Policies)>
        LoadBindingsWithHydrationAsync(IQueryable<Binding> bindings, CancellationToken ct)
    {
        var rows = await bindings.ToListAsync(ct).ConfigureAwait(false);
        var versionIds = rows.Select(b => b.PolicyVersionId).Distinct().ToList();
        if (versionIds.Count == 0)
        {
            return (rows, new Dictionary<Guid, PolicyVersion>(), new Dictionary<Guid, Policy>());
        }
        var versions = await _db.PolicyVersions.AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var policyIds = versions.Select(v => v.PolicyId).Distinct().ToList();
        var policies = await _db.Policies.AsNoTracking()
            .Where(p => policyIds.Contains(p.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (rows, versions.ToDictionary(v => v.Id), policies.ToDictionary(p => p.Id));
    }

    private static bool TryMapToScopeType(BindingTargetType targetType, out ScopeType scopeType)
    {
        switch (targetType)
        {
            case BindingTargetType.Org: scopeType = ScopeType.Org; return true;
            case BindingTargetType.Tenant: scopeType = ScopeType.Tenant; return true;
            case BindingTargetType.Repo: scopeType = ScopeType.Repo; return true;
            case BindingTargetType.Template: scopeType = ScopeType.Template; return true;
            // BindingTargetType.ScopeNode is handled separately;
            // ScopeType.Team and ScopeType.Run have no binding-target
            // equivalent (binding directly to a Team/Run goes via
            // ScopeNode references).
            default:
                scopeType = default;
                return false;
        }
    }

    private static BindingTargetType? TryMapToBindingTargetType(ScopeType type) => type switch
    {
        ScopeType.Org => BindingTargetType.Org,
        ScopeType.Tenant => BindingTargetType.Tenant,
        ScopeType.Repo => BindingTargetType.Repo,
        ScopeType.Template => BindingTargetType.Template,
        _ => null,  // Team and Run have no bridge.
    };

    // --- helpers --------------------------------------------------------

    private sealed record ChainNode(Guid Id, ScopeType Type, string Ref, int Depth);

    private sealed record BridgeKey(BindingTargetType Type, string Ref);

    private sealed record EffectiveCandidate(
        Binding Binding,
        PolicyVersion Version,
        Policy Policy,
        int Depth,
        Guid? ScopeNodeId,
        ScopeType? ScopeType);

    private sealed class BindingIdComparer : IEqualityComparer<Binding>
    {
        public bool Equals(Binding? x, Binding? y) => x?.Id == y?.Id;
        public int GetHashCode(Binding obj) => obj.Id.GetHashCode();
    }
}
