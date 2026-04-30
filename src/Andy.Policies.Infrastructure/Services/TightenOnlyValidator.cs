// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// EF-backed write-path enforcement of stricter-tightens-only (P4.4,
/// story rivoli-ai/andy-policies#32). For a proposed binding, the
/// validator resolves <c>(targetType, targetRef)</c> to the matching
/// scope node (when one exists), walks the ancestor chain, and flags
/// any ancestor that already binds the same policy as Mandatory when
/// the caller is proposing Recommended. Soft refs (a target that
/// doesn't resolve to a scope) are allowed unchecked — the catalog
/// never resolves foreign refs, per the P3 non-goal.
/// </summary>
public sealed class TightenOnlyValidator : ITightenOnlyValidator
{
    private readonly AppDbContext _db;
    private readonly IScopeService _scopes;

    public TightenOnlyValidator(AppDbContext db, IScopeService scopes)
    {
        _db = db;
        _scopes = scopes;
    }

    public async Task<TightenViolation?> ValidateCreateAsync(
        Guid policyVersionId,
        BindingTargetType targetType,
        string targetRef,
        BindStrength bindStrength,
        CancellationToken ct = default)
    {
        // The rule only fires when the proposal is Recommended — a
        // Mandatory proposal can never be a downgrade.
        if (bindStrength != BindStrength.Recommended)
        {
            return null;
        }

        // Resolve the target to a scope node. Soft refs (targets we
        // can't map) skip the walk — see header comment.
        var targetScope = await ResolveTargetScopeAsync(targetType, targetRef, ct).ConfigureAwait(false);
        if (targetScope is null)
        {
            return null;
        }

        // Find the policy id behind the proposed PolicyVersion.
        var version = await _db.PolicyVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == policyVersionId, ct)
            .ConfigureAwait(false);
        if (version is null)
        {
            // Don't pre-empt the service-layer NotFound check; let
            // BindingService raise the canonical NotFoundException
            // when it loads the version itself.
            return null;
        }

        // Walk ancestors (root-first). For each ancestor scope, check
        // both the scope-targeted and bridge bindings on the same
        // PolicyId; pick the deepest Mandatory and report it. The
        // deepest reported ancestor is the most specific cause — most
        // useful for admin triage.
        var ancestors = await _scopes.GetAncestorsAsync(targetScope.Id, ct).ConfigureAwait(false);
        if (ancestors.Count == 0)
        {
            return null;
        }

        var ancestorScopeRefs = ancestors.Select(a => $"scope:{a.Id}").ToList();
        var ancestorBridgeKeys = ancestors
            .Select(a => (Type: TryMapToBindingTargetType(a.Type), Ref: a.Ref))
            .Where(p => p.Type is not null)
            .ToList();

        // Two predicate halves: scope-targeted bindings + bridge
        // bindings on the ancestors' external Refs. Same shape as
        // BindingResolutionService from P4.3, narrowed to the same
        // PolicyId we're proposing.
        var scopeTargeted = await _db.Bindings.AsNoTracking()
            .Where(b => b.TargetType == BindingTargetType.ScopeNode
                        && b.DeletedAt == null
                        && ancestorScopeRefs.Contains(b.TargetRef)
                        && b.BindStrength == BindStrength.Mandatory
                        && _db.PolicyVersions.Any(v =>
                            v.Id == b.PolicyVersionId && v.PolicyId == version.PolicyId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var bridgeTargeted = new List<Binding>();
        if (ancestorBridgeKeys.Count > 0)
        {
            var bridgeTypes = ancestorBridgeKeys.Select(k => k.Type!.Value).Distinct().ToList();
            var bridgeRefs = ancestorBridgeKeys.Select(k => k.Ref).Distinct().ToList();
            var candidates = await _db.Bindings.AsNoTracking()
                .Where(b => bridgeTypes.Contains(b.TargetType)
                            && bridgeRefs.Contains(b.TargetRef)
                            && b.DeletedAt == null
                            && b.BindStrength == BindStrength.Mandatory
                            && _db.PolicyVersions.Any(v =>
                                v.Id == b.PolicyVersionId && v.PolicyId == version.PolicyId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var keySet = ancestorBridgeKeys
                .Select(k => (k.Type!.Value, k.Ref))
                .ToHashSet();
            bridgeTargeted = candidates
                .Where(b => keySet.Contains((b.TargetType, b.TargetRef)))
                .ToList();
        }

        if (scopeTargeted.Count == 0 && bridgeTargeted.Count == 0)
        {
            return null;
        }

        // Map each offending binding back to the depth of the ancestor
        // that bound it; report the deepest one.
        var ancestorById = ancestors.ToDictionary(a => a.Id);
        var ancestorByBridgeKey = ancestorBridgeKeys.Count == 0
            ? new Dictionary<(BindingTargetType, string), Guid>()
            : ancestors
                .Where(a => TryMapToBindingTargetType(a.Type) is not null)
                .GroupBy(a => (TryMapToBindingTargetType(a.Type)!.Value, a.Ref))
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Depth).First().Id);

        var deepest = scopeTargeted
            .Select(b =>
            {
                var idText = b.TargetRef.AsSpan("scope:".Length);
                Guid id = Guid.Parse(idText);
                return (Binding: b, ScopeNodeId: id, Depth: ancestorById[id].Depth);
            })
            .Concat(bridgeTargeted.Select(b =>
            {
                var sid = ancestorByBridgeKey[(b.TargetType, b.TargetRef)];
                return (Binding: b, ScopeNodeId: sid, Depth: ancestorById[sid].Depth);
            }))
            .OrderByDescending(t => t.Depth)
            .ThenBy(t => t.Binding.CreatedAt)
            .First();

        var policy = await _db.Policies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == version.PolicyId, ct)
            .ConfigureAwait(false);
        var policyKey = policy?.Name ?? version.PolicyId.ToString();
        var ancestorScope = ancestorById[deepest.ScopeNodeId];

        return new TightenViolation(
            OffendingAncestorBindingId: deepest.Binding.Id,
            OffendingScopeNodeId: ancestorScope.Id,
            OffendingScopeDisplayName: ancestorScope.DisplayName,
            PolicyKey: policyKey,
            Reason: $"Cannot create a Recommended binding for policy '{policyKey}' " +
                    $"at this scope — ancestor {ancestorScope.Type} '{ancestorScope.DisplayName}' " +
                    $"binds it as Mandatory (binding {deepest.Binding.Id}).");
    }

    public Task<TightenViolation?> ValidateDeleteAsync(Guid bindingId, CancellationToken ct = default)
    {
        // Tighten-only is a CREATE-time invariant; deletes cannot
        // produce a weaker downstream binding (P4.4 §reviewer-flagged
        // reconciliation). The hook stays for P5 / P6 to layer side-
        // effect checks without changing the BindingService API.
        return Task.FromResult<TightenViolation?>(null);
    }

    /// <summary>
    /// Resolve a <c>(targetType, targetRef)</c> pair to a scope node.
    /// Returns null when the target is a soft ref — i.e., a binding
    /// whose target doesn't correspond to any node in the scope tree
    /// (P3 non-scope binding pattern). Calling code allows soft-ref
    /// creates without walking; the rule only applies to hierarchical
    /// targets.
    /// </summary>
    private async Task<Domain.Entities.ScopeNode?> ResolveTargetScopeAsync(
        BindingTargetType targetType,
        string targetRef,
        CancellationToken ct)
    {
        if (targetType == BindingTargetType.ScopeNode)
        {
            // Accept "scope:{guid}" or bare guid forms.
            if (targetRef.StartsWith("scope:", StringComparison.Ordinal)
                && Guid.TryParse(targetRef.AsSpan("scope:".Length), out var fromPrefix))
            {
                return await _db.ScopeNodes.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == fromPrefix, ct)
                    .ConfigureAwait(false);
            }
            if (Guid.TryParse(targetRef, out var raw))
            {
                return await _db.ScopeNodes.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == raw, ct)
                    .ConfigureAwait(false);
            }
            return null;
        }

        var scopeType = TryMapToScopeType(targetType);
        if (scopeType is null)
        {
            return null;
        }
        return await _db.ScopeNodes.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Type == scopeType.Value && s.Ref == targetRef, ct)
            .ConfigureAwait(false);
    }

    private static ScopeType? TryMapToScopeType(BindingTargetType targetType) => targetType switch
    {
        BindingTargetType.Org => ScopeType.Org,
        BindingTargetType.Tenant => ScopeType.Tenant,
        BindingTargetType.Repo => ScopeType.Repo,
        BindingTargetType.Template => ScopeType.Template,
        // Team and Run scopes have no BindingTargetType equivalent; the
        // ScopeNode case is handled above.
        _ => null,
    };

    private static BindingTargetType? TryMapToBindingTargetType(ScopeType type) => type switch
    {
        ScopeType.Org => BindingTargetType.Org,
        ScopeType.Tenant => BindingTargetType.Tenant,
        ScopeType.Repo => BindingTargetType.Repo,
        ScopeType.Template => BindingTargetType.Template,
        _ => null,
    };
}
