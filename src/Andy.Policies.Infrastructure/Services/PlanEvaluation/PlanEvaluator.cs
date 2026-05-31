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
/// Reference <see cref="IPlanEvaluator"/> (rivoli-ai/andy-policies#232 +
/// rivoli-ai/conductor#1944).
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
///   <item>Check the 5-minute idempotency cache. The plan path keys on
///     <c>(GoalId, PlanVersion)</c>; the per-task path (#1944) extends
///     the key with the task id so a per-task re-eval is independently
///     cached and a replan still busts it.</item>
///   <item>Evaluate every registered <see cref="IPlanPredicate"/>
///     against the (optionally task-scoped) view exactly once.</item>
///   <item>Resolve the workspace's applicable policies via the existing
///     <see cref="IBindingResolutionService"/> (P4.3).</item>
///   <item>For each effective policy in resolution order, load its
///     <c>RulesJson</c> + criticality, parse the <c>planEvaluation</c>
///     block, and evaluate its decision rules against the predicate
///     trace. The first policy whose rule fires <c>approve</c> or
///     <c>reject</c> wins.</item>
///   <item>Default: <c>manual</c>.</item>
/// </list>
/// <para>
/// The plan-finalize and per-task surfaces share one evaluator core
/// (<see cref="EvaluateCoreAsync"/>): the binary
/// <see cref="EvaluatePlanResponse"/> and the structured
/// <see cref="ComplianceAssessment"/> are two projections of the same
/// firing-rule trace (rivoli-ai/conductor#1944 — no forked evaluator).
/// </para>
/// </remarks>
public sealed class PlanEvaluator : IPlanEvaluator
{
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromMinutes(5);

    private readonly ITasksPlanClient _tasks;
    private readonly IBindingResolutionService _bindings;
    private readonly AppDbContext _db;
    private readonly IEnumerable<IPlanPredicate> _predicates;
    private readonly IComplianceScorer _scorer;
    private readonly IComplianceAuditPublisher _auditPublisher;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PlanEvaluator> _log;
    private readonly TimeProvider _clock;

    public PlanEvaluator(
        ITasksPlanClient tasks,
        IBindingResolutionService bindings,
        AppDbContext db,
        IEnumerable<IPlanPredicate> predicates,
        IComplianceScorer scorer,
        IComplianceAuditPublisher auditPublisher,
        IMemoryCache cache,
        ILogger<PlanEvaluator> log,
        TimeProvider? clock = null)
    {
        _tasks = tasks;
        _bindings = bindings;
        _db = db;
        _predicates = predicates;
        _scorer = scorer;
        _auditPublisher = auditPublisher;
        _cache = cache;
        _log = log;
        _clock = clock ?? TimeProvider.System;
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

        var core = await EvaluateCoreAsync(view, ct).ConfigureAwait(false);

        var response = new EvaluatePlanResponse(
            Decision: core.Decision,
            PolicyId: core.FiringPolicyKey,
            Predicates: core.PredicateTrace);

        _cache.Set(cacheKey, response, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = IdempotencyTtl,
        });

        // rivoli-ai/conductor#1945 (TX F7.2): inject the structured
        // assessment + tamper-evident audit-chain segment into andy-docs
        // under role:Audit, linked to the goal. Best-effort — the
        // publisher swallows + degrades on failure, never altering the
        // decision. Only on cache miss so a re-eval doesn't re-publish.
        var assessment = ProjectAssessment(core);
        await _auditPublisher
            .PublishPlanAsync(view.GoalId, view.PlanVersion, assessment, ct)
            .ConfigureAwait(false);

        return response;
    }

    public async Task<EvaluateTaskResponse?> EvaluateTaskAsync(
        Guid goalId, Guid taskId, CancellationToken ct = default)
    {
        var view = await _tasks.FetchGoalViewAsync(goalId, ct).ConfigureAwait(false);
        if (view is null) return null;

        // Scope the predicate inputs down to the single task. A goal that
        // doesn't contain the task is a 404 (the controller translates a
        // null result) — never a silent "no violations" pass.
        var task = view.Tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task is null)
        {
            _log.LogDebug(
                "task eval: goal {GoalId} does not contain task {TaskId}", goalId, taskId);
            return null;
        }

        // Cache key includes the task id (and plan version) so a per-task
        // re-eval is cached independently and a replan busts it.
        var cacheKey = $"task-eval:{view.GoalId:D}:{taskId:D}:{view.PlanVersion}";
        if (_cache.TryGetValue(cacheKey, out EvaluateTaskResponse? cached) && cached is not null)
        {
            return cached;
        }

        var scopedView = ScopeToTask(view, task);
        var core = await EvaluateCoreAsync(scopedView, ct).ConfigureAwait(false);

        var assessment = ProjectAssessment(core);

        var response = new EvaluateTaskResponse(goalId, taskId, assessment);

        _cache.Set(cacheKey, response, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = IdempotencyTtl,
        });

        // rivoli-ai/conductor#1945 (TX F7.2): inject the structured
        // assessment + tamper-evident audit-chain segment into andy-docs
        // under role:Audit, linked to the goal AND the task. Best-effort
        // — never alters the decision. Only on cache miss.
        await _auditPublisher
            .PublishTaskAsync(goalId, taskId, view.PlanVersion, assessment, ct)
            .ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// Project the shared evaluation core into the structured
    /// <see cref="ComplianceAssessment"/> — fold the scored violations
    /// through <see cref="IComplianceScorer"/> for the aggregate tier +
    /// score. Used by both the per-task surface (its wire response) and
    /// the plan-finalize surface (the audit injection per
    /// rivoli-ai/conductor#1945; plan-finalize's own wire response stays
    /// the binary <see cref="EvaluatePlanResponse"/>).
    /// </summary>
    private ComplianceAssessment ProjectAssessment(EvaluationCore core)
    {
        var scored = core.Violations
            .Select(v => new ScoredViolation(v.Wire, v.Criticality))
            .ToList();
        var risk = _scorer.Score(scored);

        return new ComplianceAssessment(
            Decision: core.Decision,
            RiskTier: risk.Tier,
            RiskScore: risk.Score,
            Violations: core.Violations.Select(v => v.Wire).ToList(),
            Predicates: core.PredicateTrace,
            EvaluatedAt: _clock.GetUtcNow());
    }

    private static PlanEvaluationGoalView ScopeToTask(
        PlanEvaluationGoalView view, PlanEvaluationTaskView task) => view with
        {
            Tasks = new List<PlanEvaluationTaskView> { task },
        };

    /// <summary>
    /// Shared core: evaluate predicates once, resolve effective policies,
    /// then walk decision rules to find the firing decision + the
    /// violations it depended on. Both the plan and task entry points
    /// project this single result.
    /// </summary>
    private async Task<EvaluationCore> EvaluateCoreAsync(
        PlanEvaluationGoalView view, CancellationToken ct)
    {
        // Evaluate every predicate once. Stable order = registration
        // order from DI. Surfaced on the response so callers see the
        // full predicate trace, not just whatever the winning rule named.
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

        var effective = await ResolveWorkspacePoliciesAsync(view, ct).ConfigureAwait(false);

        var (decision, firingPolicyKey, violations) =
            await EvaluateDecisionAsync(effective, byName, ct).ConfigureAwait(false);

        return new EvaluationCore(
            decision,
            firingPolicyKey,
            predicateResults.Select(r => r.Dto).ToList(),
            violations);
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

        var set = await _bindings.ResolveForTargetAsync(
            BindingTargetType.ScopeNode,
            $"scope:{view.WorkspaceContainerId}",
            ct).ConfigureAwait(false);

        return set.Policies;
    }

    private async Task<(string Decision, string? PolicyKey, IReadOnlyList<ScoredViolationCore> Violations)>
        EvaluateDecisionAsync(
            IReadOnlyList<EffectivePolicyDto> effective,
            IReadOnlyDictionary<string, PredicateEvaluation> byName,
            CancellationToken ct)
    {
        if (effective.Count == 0)
        {
            return ("manual", null, Array.Empty<ScoredViolationCore>());
        }

        // Load the RulesJson + criticality for every effective policy
        // version in one round-trip. Criticality (Severity) feeds the
        // compliance scorer's weight fold (#1944).
        var versionIds = effective.Select(e => e.PolicyVersionId).Distinct().ToList();
        var metaByVersionId = await _db.PolicyVersions
            .AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .Select(v => new { v.Id, v.RulesJson, v.Severity })
            .ToDictionaryAsync(v => v.Id, v => new { v.RulesJson, v.Severity }, ct)
            .ConfigureAwait(false);

        var violations = new List<ScoredViolationCore>();

        foreach (var policy in effective)
        {
            if (!metaByVersionId.TryGetValue(policy.PolicyVersionId, out var meta))
            {
                continue;
            }

            var rules = PolicyRulesDslParser.TryParse(meta.RulesJson);
            if (rules?.DecisionRules is null) continue;

            foreach (var rule in rules.DecisionRules)
            {
                if (!RuleFires(rule, byName)) continue;

                var normalized = NormalizeDecision(rule.Decision);

                // A firing reject/manual rule produces violations for each
                // predicate it depended on that did NOT pass (fail or
                // unevaluable). approve rules never produce violations.
                if (normalized is "reject" or "manual")
                {
                    CollectViolations(rule, byName, policy, meta.Severity, normalized, violations);
                }

                if (normalized is "approve" or "reject")
                {
                    // First terminal decision wins; ties broken by
                    // resolution order (P4.3 tighten-only fold).
                    return (normalized, policy.PolicyKey, violations);
                }
                // "manual" rules fall through (their violations are kept);
                // unknown decisions are ignored (catalog tolerance).
            }
        }

        return ("manual", null, violations);
    }

    /// <summary>
    /// For a firing rule, surface a violation per referenced predicate
    /// that did not pass. <c>requireAll</c> predicates that are
    /// <c>fail</c>/<c>unevaluable</c> and <c>requireNone</c> predicates
    /// that are <c>fail</c>/<c>unevaluable</c> (i.e. the conditions that
    /// made the rule fire) are the proximate cause. Unevaluable is
    /// surfaced as a violation exactly like fail — never a silent pass.
    /// </summary>
    private static void CollectViolations(
        DecisionRule rule,
        IReadOnlyDictionary<string, PredicateEvaluation> byName,
        EffectivePolicyDto policy,
        Severity criticality,
        string decision,
        List<ScoredViolationCore> sink)
    {
        void Add(string predicate)
        {
            if (!byName.TryGetValue(predicate, out var ev))
            {
                // Unknown predicate cannot fire a requireAll rule (handled
                // by RuleFires); nothing to record.
                return;
            }
            if (ev.Outcome == PredicateOutcome.Pass) return;

            var outcomeWire = ev.Outcome == PredicateOutcome.Unevaluable
                ? "unevaluable"
                : "fail";
            var reason = ev.Outcome == PredicateOutcome.Unevaluable
                ? "data unavailable"
                : ev.Reason;

            sink.Add(new ScoredViolationCore(
                new ComplianceViolation(
                    PolicyKey: policy.PolicyKey,
                    Predicate: predicate,
                    Outcome: outcomeWire,
                    Decision: decision,
                    Reason: reason),
                criticality));
        }

        if (rule.RequireNone is { Count: > 0 })
        {
            foreach (var name in rule.RequireNone) Add(name);
        }

        if (rule.RequireAll is { Count: > 0 })
        {
            // A requireAll rule fires only when ALL pass, so requireAll
            // never produces violations on a firing rule. Kept for
            // completeness/symmetry — Add() no-ops on Pass.
            foreach (var name in rule.RequireAll) Add(name);
        }
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

    /// <summary>
    /// Result of the shared evaluation core. <see cref="Violations"/>
    /// carry the criticality (internal scoring input) alongside the wire
    /// violation; the plan projection ignores them, the task projection
    /// folds them through the scorer.
    /// </summary>
    private sealed record EvaluationCore(
        string Decision,
        string? FiringPolicyKey,
        IReadOnlyList<PredicateResultDto> PredicateTrace,
        IReadOnlyList<ScoredViolationCore> Violations);

    private sealed record ScoredViolationCore(
        ComplianceViolation Wire,
        Severity Criticality);
}
