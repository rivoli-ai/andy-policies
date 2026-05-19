// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.PlanEvaluation;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.Services.PlanEvaluation;

/// <summary>
/// Reference <see cref="IPlanEvaluator"/> (rivoli-ai/andy-policies#232).
/// </summary>
/// <remarks>
/// <para>
/// Pipeline (in order):
/// </para>
/// <list type="number">
///   <item>Fetch the projected goal view from andy-tasks via
///     <see cref="ITasksPlanClient"/>. Return null on 404 — controller
///     translates that to 404 so the caller can distinguish "missing
///     goal" from "no policies registered".</item>
///   <item>Check the 5-minute idempotency cache keyed on
///     <c>(GoalId, PlanVersion)</c>. Hit returns the cached response
///     verbatim; the planVersion in the cache key guarantees that a
///     replanned goal sees a fresh decision.</item>
///   <item>Evaluate every registered <see cref="IPlanPredicate"/>
///     against the view exactly once. The same predicate trace is
///     consumed by every policy's rule list AND surfaced to the caller
///     so the wire response carries the full set, not just the rules
///     the winning policy referenced.</item>
///   <item>Resolve the workspace's applicable policies via the existing
///     <see cref="IBindingResolutionService"/> (P4.3) — the bridge that
///     walks the scope chain from the workspace's container reference
///     and folds tighten-only.</item>
///   <item>For each effective policy in resolution order, load its
///     <c>RulesJson</c>, parse the <c>planEvaluation</c> block, and
///     evaluate its decision rules against the predicate trace. The
///     first policy whose rule fires <c>approve</c> or <c>reject</c>
///     wins; ties are broken by resolution order (Mandatory wins, then
///     deeper scope, then earlier <c>CreatedAt</c> — already enforced
///     by P4.3's tighten-only fold).</item>
///   <item>Default: <c>manual</c>.</item>
/// </list>
/// </remarks>
public sealed class PlanEvaluator : IPlanEvaluator
{
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromMinutes(5);

    private readonly ITasksPlanClient _tasks;
    private readonly IBindingResolutionService _bindings;
    private readonly AppDbContext _db;
    private readonly IEnumerable<IPlanPredicate> _predicates;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PlanEvaluator> _log;

    public PlanEvaluator(
        ITasksPlanClient tasks,
        IBindingResolutionService bindings,
        AppDbContext db,
        IEnumerable<IPlanPredicate> predicates,
        IMemoryCache cache,
        ILogger<PlanEvaluator> log)
    {
        _tasks = tasks;
        _bindings = bindings;
        _db = db;
        _predicates = predicates;
        _cache = cache;
        _log = log;
    }

    public async Task<EvaluatePlanResponse?> EvaluatePlanAsync(
        Guid goalId, CancellationToken ct = default)
    {
        var view = await _tasks.FetchGoalViewAsync(goalId, ct).ConfigureAwait(false);
        if (view is null) return null;

        var cacheKey = $"plan-eval:{view.GoalId:D}:{view.PlanVersion}";
        if (_cache.TryGetValue(cacheKey, out EvaluatePlanResponse? cached) && cached is not null)
        {
            return cached;
        }

        // Evaluate every predicate once. Stable order = registration
        // order from DI. Stored on the response so callers see the
        // full predicate trace, not just whatever the winning rule
        // happened to name.
        var predicateResults = _predicates
            .Select(p =>
            {
                var eval = p.Evaluate(view);
                return new
                {
                    p.Name,
                    Evaluation = eval,
                    Dto = ToDto(p.Name, eval),
                };
            })
            .ToList();

        var byName = predicateResults.ToDictionary(r => r.Name, r => r.Evaluation, StringComparer.Ordinal);

        // Resolve workspace policies via the binding chain. The
        // workspace's container id is mapped to a `scope:{guid}` target
        // ref — same shape the existing P4 resolver uses for scope
        // nodes — but a workspace with no container reference at all
        // means we can't resolve anything; that falls through to
        // manual with no policy fired.
        var effective = await ResolveWorkspacePoliciesAsync(view, ct).ConfigureAwait(false);

        var (decision, firingPolicyId) = await EvaluateDecisionAsync(effective, byName, ct).ConfigureAwait(false);

        var response = new EvaluatePlanResponse(
            Decision: decision,
            PolicyId: firingPolicyId,
            Predicates: predicateResults.Select(r => r.Dto).ToList());

        // Cache only after the response is built. AbsoluteExpirationRelativeToNow
        // — sliding would let a single hot goal pin a decision longer
        // than the 5-minute contract.
        _cache.Set(cacheKey, response, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = IdempotencyTtl,
        });

        return response;
    }

    private async Task<IReadOnlyList<EffectivePolicyDto>> ResolveWorkspacePoliciesAsync(
        PlanEvaluationGoalView view, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(view.WorkspaceContainerId))
        {
            _log.LogDebug(
                "plan eval for goal {GoalId}: no workspace container id; skipping resolution",
                view.GoalId);
            return Array.Empty<EffectivePolicyDto>();
        }

        // The workspace's container id maps to a Scope target — that's
        // the canonical "everything for this workspace" anchor. If
        // andy-tasks ever splits its workspace shape into multiple
        // scopes we resolve them in a future iteration; for #232 a
        // single anchor matches the existing binding model.
        var set = await _bindings.ResolveForTargetAsync(
            BindingTargetType.ScopeNode,
            $"scope:{view.WorkspaceContainerId}",
            ct).ConfigureAwait(false);

        return set.Policies;
    }

    private async Task<(string Decision, string? PolicyId)> EvaluateDecisionAsync(
        IReadOnlyList<EffectivePolicyDto> effective,
        IReadOnlyDictionary<string, PredicateEvaluation> byName,
        CancellationToken ct)
    {
        if (effective.Count == 0) return ("manual", null);

        // Load the RulesJson for every effective policy version in one
        // round-trip. Order preserved by re-projecting through the
        // input list.
        var versionIds = effective.Select(e => e.PolicyVersionId).Distinct().ToList();
        var rulesByVersionId = await _db.PolicyVersions
            .AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .Select(v => new { v.Id, v.RulesJson })
            .ToDictionaryAsync(v => v.Id, v => v.RulesJson, ct)
            .ConfigureAwait(false);

        foreach (var policy in effective)
        {
            if (!rulesByVersionId.TryGetValue(policy.PolicyVersionId, out var rulesJson))
            {
                continue;
            }

            var rules = PolicyRulesDslParser.TryParse(rulesJson);
            if (rules?.DecisionRules is null) continue;

            foreach (var rule in rules.DecisionRules)
            {
                if (!RuleFires(rule, byName)) continue;

                var normalized = NormalizeDecision(rule.Decision);
                if (normalized is "approve" or "reject")
                {
                    // PolicyKey is the wire-stable string identifier
                    // ("policy.name") rather than the internal GUID;
                    // it matches what callers see in catalog listings
                    // and audit trails.
                    return (normalized, policy.PolicyKey);
                }
                // "manual" rules fall through silently; otherwise
                // unknown decisions are ignored (catalog tolerance
                // beats strictness here).
            }
        }

        return ("manual", null);
    }

    private static bool RuleFires(
        DecisionRule rule,
        IReadOnlyDictionary<string, PredicateEvaluation> byName)
    {
        if (rule.RequireAll is { Count: > 0 })
        {
            foreach (var name in rule.RequireAll)
            {
                if (!byName.TryGetValue(name, out var ev) ||
                    ev.Outcome != PredicateOutcome.Pass)
                {
                    return false;
                }
            }
        }

        if (rule.RequireNone is { Count: > 0 })
        {
            foreach (var name in rule.RequireNone)
            {
                // Semantic: rule fires only when NONE of these predicates
                // passes. An explicit Pass disqualifies the rule; Fail,
                // Unevaluable, and unknown-by-name all leave the rule
                // eligible to fire. (Unevaluable is conservative —
                // "couldn't prove it passed" satisfies "must not pass".)
                if (byName.TryGetValue(name, out var ev) &&
                    ev.Outcome == PredicateOutcome.Pass)
                {
                    return false;
                }
            }
        }

        // A rule with neither RequireAll nor RequireNone is degenerate
        // (always fires). Allow it — callers may want a catch-all
        // "manual" rule as documentation of intent.
        return true;
    }

    private static string NormalizeDecision(string? raw) =>
        raw?.Trim().ToLowerInvariant() ?? "manual";

    private static PredicateResultDto ToDto(string name, PredicateEvaluation eval) => new(
        Name: name,
        Passed: eval.Outcome == PredicateOutcome.Pass,
        Reason: eval.Outcome == PredicateOutcome.Unevaluable
            ? "data unavailable"
            : eval.Reason);
}
