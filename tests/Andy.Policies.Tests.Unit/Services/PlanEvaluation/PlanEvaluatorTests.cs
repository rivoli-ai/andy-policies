// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.PlanEvaluation;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Services.PlanEvaluation;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services.PlanEvaluation;

/// <summary>
/// Unit tests for <see cref="PlanEvaluator"/> — the orchestrator that
/// stitches the andy-tasks client, the binding-resolution chain, the
/// predicate registry, and the idempotency cache into the wire
/// response. All collaborators are stubbed; the goal is to assert the
/// decision logic and the cache contract, not the wire shapes (those
/// live in the integration tests).
/// </summary>
public class PlanEvaluatorTests
{
    private static readonly IReadOnlyList<IPlanPredicate> AllPredicates = new IPlanPredicate[]
    {
        new AllTasksReadOnlyPredicate(),
        new WorkspaceIsSandboxPredicate(),
        new NoProductionDeployPredicate(),
        new AllAgentsApprovedPredicate(),
        new RespectsCostBudgetPredicate(),
    };

    [Fact]
    public async Task Returns_null_when_andy_tasks_does_not_recognise_the_goal()
    {
        var goalId = Guid.NewGuid();
        var (eval, _) = NewEvaluator(
            tasks: new StubTasksClient(_ => null),
            bindings: new StubBindings(Array.Empty<EffectivePolicyDto>()));

        var result = await eval.EvaluatePlanAsync(goalId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Defaults_to_manual_when_no_policies_are_bound()
    {
        var view = AView();
        var (eval, _) = NewEvaluator(
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(Array.Empty<EffectivePolicyDto>()));

        var result = await eval.EvaluatePlanAsync(view.GoalId);

        result.Should().NotBeNull();
        result!.Decision.Should().Be("manual");
        result.PolicyId.Should().BeNull();
        result.Predicates.Should().HaveCount(5,
            "every registered predicate must show up in the trace");
    }

    [Fact]
    public async Task Fires_approve_when_first_policys_rule_passes_all_required_predicates()
    {
        // Sandboxed, read-only plan, under budget, agent approved, no prod.
        var view = AView(
            tasks: new[] { ATask(tools: new[] { "read-file" }, agentId: "coder") },
            tier: "sandbox",
            approved: new[] { "coder" },
            budget: 10m,
            estimatedCost: 1m);

        var rules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "approve",
                "requireAll": ["allTasksReadOnly", "workspaceIsSandbox", "respectsCostBudget"] }
            ] } }
        """;

        await using var db = InMemoryDbFixture.Create();
        var policyKey = "safe-sandbox-autoapprove";
        var version = SeedPolicyVersion(db, policyKey, rules);

        var (eval, _) = NewEvaluator(
            db: db,
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(new[]
            {
                Effective(version, policyKey),
            }));

        var result = await eval.EvaluatePlanAsync(view.GoalId);

        result!.Decision.Should().Be("approve");
        result.PolicyId.Should().Be(policyKey);
    }

    [Fact]
    public async Task Fires_reject_when_a_negative_rule_matches()
    {
        var view = AView(
            tasks: new[] { ATask(env: "prod") },
            tier: "production",
            approved: new[] { "coder" });

        var rules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "reject",
                "requireNone": ["noProductionDeploy"] }
            ] } }
        """;

        await using var db = InMemoryDbFixture.Create();
        var key = "no-prod-deploy";
        var version = SeedPolicyVersion(db, key, rules);

        var (eval, _) = NewEvaluator(
            db: db,
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(new[] { Effective(version, key) }));

        var result = await eval.EvaluatePlanAsync(view.GoalId);

        result!.Decision.Should().Be("reject");
        result.PolicyId.Should().Be(key);
    }

    [Fact]
    public async Task First_matching_policy_wins_across_resolved_set()
    {
        // Two policies bound; first rejects, second would approve. The
        // evaluator must surface the reject and not look further.
        var view = AView(
            tasks: new[] { ATask(env: "prod", tools: new[] { "read-file" }, agentId: "coder") },
            tier: "sandbox",
            approved: new[] { "coder" },
            budget: 100m,
            estimatedCost: 1m);

        var rejectRules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "reject", "requireNone": ["noProductionDeploy"] }
            ] } }
        """;

        var approveRules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "approve", "requireAll": ["workspaceIsSandbox"] }
            ] } }
        """;

        await using var db = InMemoryDbFixture.Create();
        var rejectVersion = SeedPolicyVersion(db, "no-prod", rejectRules);
        var approveVersion = SeedPolicyVersion(db, "sandbox-ok", approveRules);

        var (eval, _) = NewEvaluator(
            db: db,
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(new[]
            {
                Effective(rejectVersion, "no-prod"),
                Effective(approveVersion, "sandbox-ok"),
            }));

        var result = await eval.EvaluatePlanAsync(view.GoalId);

        result!.Decision.Should().Be("reject");
        result.PolicyId.Should().Be("no-prod");
    }

    [Fact]
    public async Task Surfaces_data_unavailable_on_predicates_with_missing_inputs()
    {
        // No tier, no approved list, no budget — three predicates
        // surface "data unavailable". The fourth and fifth still
        // evaluate. The wire response must reflect this verbatim.
        var view = AView(
            tasks: new[] { ATask(tools: new[] { "read-file" }) },
            tier: null,
            approved: null,
            budget: null,
            estimatedCost: null);

        var (eval, _) = NewEvaluator(
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(Array.Empty<EffectivePolicyDto>()));

        var result = await eval.EvaluatePlanAsync(view.GoalId);

        var byName = result!.Predicates.ToDictionary(p => p.Name);
        byName[PlanPredicateNames.WorkspaceIsSandbox].Passed.Should().BeFalse();
        byName[PlanPredicateNames.WorkspaceIsSandbox].Reason.Should().Be("data unavailable");
        byName[PlanPredicateNames.AllAgentsApproved].Passed.Should().BeFalse();
        byName[PlanPredicateNames.AllAgentsApproved].Reason.Should().Be("data unavailable");
        byName[PlanPredicateNames.RespectsCostBudget].Passed.Should().BeFalse();
        byName[PlanPredicateNames.RespectsCostBudget].Reason.Should().Be("data unavailable");

        // The other two evaluate normally.
        byName[PlanPredicateNames.AllTasksReadOnly].Passed.Should().BeTrue();
        byName[PlanPredicateNames.NoProductionDeploy].Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Caches_decision_for_5_minutes_per_goal_and_plan_version()
    {
        var view = AView();
        var stub = new StubTasksClient(_ => view);
        var (eval, _) = NewEvaluator(
            tasks: stub,
            bindings: new StubBindings(Array.Empty<EffectivePolicyDto>()));

        var first = await eval.EvaluatePlanAsync(view.GoalId);
        var second = await eval.EvaluatePlanAsync(view.GoalId);

        first.Should().NotBeNull();
        second.Should().BeSameAs(first,
            "the cache must return the same response instance within the TTL");
        // The tasks client is still hit twice — the cache key is built
        // from the GoalView, so the projected view drives idempotency,
        // not the request envelope. This is the desired contract: a
        // replanned goal (new PlanVersion) bypasses the cache.
        stub.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Cache_key_includes_plan_version_so_replan_bypasses_cache()
    {
        var goalId = Guid.NewGuid();
        var calls = 0;
        var stub = new StubTasksClient(_ =>
        {
            calls++;
            return AView(goalId: goalId, planVersion: calls.ToString());
        });

        var (eval, _) = NewEvaluator(
            tasks: stub,
            bindings: new StubBindings(Array.Empty<EffectivePolicyDto>()));

        var first = await eval.EvaluatePlanAsync(goalId);
        var second = await eval.EvaluatePlanAsync(goalId);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Should().NotBeSameAs(second,
            "different plan versions must produce distinct cache entries");
    }

    [Fact]
    public async Task Malformed_RulesJson_is_skipped_silently_and_evaluation_continues()
    {
        var view = AView(tier: "sandbox", tasks: new[] { ATask(tools: new[] { "read-file" }) });

        await using var db = InMemoryDbFixture.Create();
        var bad = SeedPolicyVersion(db, "malformed", "{ not valid json");
        var goodRules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "approve", "requireAll": ["workspaceIsSandbox"] }
            ] } }
        """;
        var good = SeedPolicyVersion(db, "good", goodRules);

        var (eval, _) = NewEvaluator(
            db: db,
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(new[]
            {
                Effective(bad, "malformed"),
                Effective(good, "good"),
            }));

        var result = await eval.EvaluatePlanAsync(view.GoalId);

        result!.Decision.Should().Be("approve");
        result.PolicyId.Should().Be("good");
    }

    [Fact]
    public async Task Unknown_predicate_in_rule_does_not_fire_the_rule()
    {
        var view = AView(tier: "sandbox");

        var rules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "approve",
                "requireAll": ["thisPredicateDoesNotExist", "workspaceIsSandbox"] }
            ] } }
        """;

        await using var db = InMemoryDbFixture.Create();
        var version = SeedPolicyVersion(db, "p", rules);

        var (eval, _) = NewEvaluator(
            db: db,
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(new[] { Effective(version, "p") }));

        var result = await eval.EvaluatePlanAsync(view.GoalId);

        // Unknown predicate → rule doesn't fire → fall through to manual.
        result!.Decision.Should().Be("manual");
    }

    // ---- helpers ----------------------------------------------------------

    private static (PlanEvaluator Eval, IMemoryCache Cache) NewEvaluator(
        ITasksPlanClient tasks,
        IBindingResolutionService bindings,
        Andy.Policies.Infrastructure.Data.AppDbContext? db = null,
        IReadOnlyList<IPlanPredicate>? predicates = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var dbCtx = db ?? InMemoryDbFixture.Create();
        var eval = new PlanEvaluator(
            tasks, bindings, dbCtx,
            (predicates ?? AllPredicates), cache, NullLogger<PlanEvaluator>.Instance);
        return (eval, cache);
    }

    private static PlanEvaluationGoalView AView(
        Guid? goalId = null,
        string planVersion = "1",
        IEnumerable<PlanEvaluationTaskView>? tasks = null,
        string? tier = null,
        IReadOnlyList<string>? approved = null,
        decimal? budget = null,
        decimal? estimatedCost = null,
        string? container = "ctr-1") => new(
        GoalId: goalId ?? Guid.NewGuid(),
        PlanVersion: planVersion,
        WorkspaceContainerId: container,
        WorkspaceTier: tier,
        WorkspaceApprovedAgentIds: approved,
        WorkspaceCostBudgetUsd: budget,
        Tasks: tasks?.ToList() ?? new List<PlanEvaluationTaskView>(),
        EstimatedCostUsd: estimatedCost);

    private static PlanEvaluationTaskView ATask(
        IEnumerable<string>? tools = null,
        string? agentId = "coder",
        string? env = null) => new(
        TaskId: Guid.NewGuid(),
        EffectiveAgentId: agentId,
        ToolsAllowed: tools?.ToList() ?? new List<string> { "read-file" },
        TargetEnv: env);

    private static PolicyVersion SeedPolicyVersion(
        Andy.Policies.Infrastructure.Data.AppDbContext db, string policyKey, string rulesJson)
    {
        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = policyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "unit",
        };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            Version = 1,
            State = LifecycleState.Active,
            Enforcement = EnforcementLevel.Should,
            Severity = Severity.Moderate,
            Scopes = new List<string>(),
            Summary = "fixture",
            RulesJson = rulesJson,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "unit",
            ProposerSubjectId = "unit",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        db.SaveChanges();
        return version;
    }

    private static EffectivePolicyDto Effective(PolicyVersion version, string key) => new(
        PolicyId: version.PolicyId,
        PolicyVersionId: version.Id,
        PolicyKey: key,
        Version: version.Version,
        BindStrength: BindStrength.Recommended,
        SourceBindingId: Guid.NewGuid(),
        SourceScopeNodeId: null,
        SourceScopeType: null,
        SourceDepth: 0);

    private sealed class StubTasksClient : ITasksPlanClient
    {
        private readonly Func<Guid, PlanEvaluationGoalView?> _project;
        public int CallCount { get; private set; }

        public StubTasksClient(Func<Guid, PlanEvaluationGoalView?> project) => _project = project;

        public Task<PlanEvaluationGoalView?> FetchGoalViewAsync(Guid goalId, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_project(goalId));
        }
    }

    private sealed class StubBindings : IBindingResolutionService
    {
        private readonly IReadOnlyList<EffectivePolicyDto> _policies;
        public StubBindings(IReadOnlyList<EffectivePolicyDto> policies) => _policies = policies;

        public Task<EffectivePolicySetDto> ResolveForScopeAsync(Guid scopeNodeId, CancellationToken ct = default)
            => Task.FromResult(new EffectivePolicySetDto(scopeNodeId, _policies));

        public Task<EffectivePolicySetDto> ResolveForTargetAsync(
            BindingTargetType targetType, string targetRef, CancellationToken ct = default)
            => Task.FromResult(new EffectivePolicySetDto(null, _policies));
    }
}
