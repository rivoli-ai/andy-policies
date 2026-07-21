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
/// Unit tests for <see cref="PlanEvaluator.EvaluateTaskAsync"/> — the
/// per-task / per-run evaluation entry point (rivoli-ai/conductor#1944 —
/// TX F7.1). Asserts: the structured <see cref="ComplianceAssessment"/>
/// projection (violations + risk tier/score), the back-compat decision
/// string, that unevaluable predicates surface as violations (never a
/// silent pass), task-scoping (404 for unknown task), and the
/// per-task idempotency cache keyed on (goal, task, planVersion).
/// </summary>
public class PlanEvaluatorTaskTests
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
    public async Task Returns_null_when_goal_is_unknown()
    {
        var (eval, _) = NewEvaluator(
            tasks: new StubTasksClient(_ => null),
            bindings: new StubBindings(Array.Empty<EffectivePolicyDto>()));

        var result = await eval.EvaluateTaskAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_goal_does_not_contain_the_task()
    {
        var task = ATask();
        var view = AView(tasks: new[] { task });
        var (eval, _) = NewEvaluator(
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(Array.Empty<EffectivePolicyDto>()));

        // Ask for a task id that isn't in the goal.
        var result = await eval.EvaluateTaskAsync(view.GoalId, Guid.NewGuid());

        result.Should().BeNull("a task absent from the goal must 404, not silently pass");
    }

    [Fact]
    public async Task Clean_task_with_no_policies_has_none_tier_and_no_violations()
    {
        var task = ATask(tools: new[] { "read-file" });
        var view = AView(tasks: new[] { task }, tier: "sandbox", approved: new[] { "coder" }, budget: 10m, estimatedCost: 1m);
        var (eval, _) = NewEvaluator(
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(Array.Empty<EffectivePolicyDto>()));

        var result = await eval.EvaluateTaskAsync(view.GoalId, task.TaskId);

        result.Should().NotBeNull();
        var a = result!.Assessment;
        a.Decision.Should().Be("manual");
        a.RiskTier.Should().Be(RiskTier.None);
        a.RiskScore.Should().Be(0);
        a.Violations.Should().BeEmpty();
        a.Predicates.Should().HaveCount(5);
        a.EvaluatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task Reject_rule_produces_critical_violation_and_decision()
    {
        // A prod-targeting task; the noProductionDeploy predicate fails,
        // so a requireNone:[noProductionDeploy] reject rule fires. The
        // policy is Critical severity → critical risk tier.
        var task = ATask(env: "prod");
        var view = AView(tasks: new[] { task }, tier: "production");

        var rules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "reject", "requireNone": ["noProductionDeploy"] }
            ] } }
        """;

        await using var db = InMemoryDbFixture.Create();
        var key = "no-prod-deploy";
        var version = SeedPolicyVersion(db, key, rules, Severity.Critical);

        var (eval, _) = NewEvaluator(
            db: db,
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(new[] { Effective(version, key) }));

        var result = await eval.EvaluateTaskAsync(view.GoalId, task.TaskId);

        var a = result!.Assessment;
        a.Decision.Should().Be("reject");
        a.RiskTier.Should().Be(RiskTier.Critical);
        a.RiskScore.Should().Be(20);
        a.Violations.Should().ContainSingle();
        var v = a.Violations[0];
        v.PolicyKey.Should().Be(key);
        v.Predicate.Should().Be(PlanPredicateNames.NoProductionDeploy);
        v.Outcome.Should().Be("fail");
        v.Decision.Should().Be("reject");
    }

    [Fact]
    public async Task Unevaluable_predicate_is_surfaced_as_a_violation_not_a_silent_pass()
    {
        // No tier → workspaceIsSandbox is unevaluable. A reject rule that
        // requireNone:[workspaceIsSandbox] fires (unevaluable satisfies
        // "must not pass"), and the unevaluable predicate is recorded as a
        // violation with outcome "unevaluable".
        var task = ATask(tools: new[] { "read-file" });
        var view = AView(tasks: new[] { task }, tier: null);

        var rules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "reject", "requireNone": ["workspaceIsSandbox"] }
            ] } }
        """;

        await using var db = InMemoryDbFixture.Create();
        var version = SeedPolicyVersion(db, "needs-sandbox", rules, Severity.Moderate);

        var (eval, _) = NewEvaluator(
            db: db,
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(new[] { Effective(version, "needs-sandbox") }));

        var result = await eval.EvaluateTaskAsync(view.GoalId, task.TaskId);

        var a = result!.Assessment;
        a.Decision.Should().Be("reject");
        a.Violations.Should().ContainSingle(v =>
            v.Predicate == PlanPredicateNames.WorkspaceIsSandbox &&
            v.Outcome == "unevaluable" &&
            v.Reason == "data unavailable");
        a.RiskTier.Should().BeOneOf(RiskTier.High); // moderate + reject
    }

    [Fact]
    public async Task Approve_rule_produces_no_violations_and_none_tier()
    {
        var task = ATask(tools: new[] { "read-file" }, agentId: "coder");
        var view = AView(tasks: new[] { task }, tier: "sandbox", approved: new[] { "coder" }, budget: 100m, estimatedCost: 1m);

        var rules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "approve", "requireAll": ["allTasksReadOnly", "workspaceIsSandbox"] }
            ] } }
        """;

        await using var db = InMemoryDbFixture.Create();
        var version = SeedPolicyVersion(db, "sandbox-ok", rules, Severity.Moderate);

        var (eval, _) = NewEvaluator(
            db: db,
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(new[] { Effective(version, "sandbox-ok") }));

        var result = await eval.EvaluateTaskAsync(view.GoalId, task.TaskId);

        var a = result!.Assessment;
        a.Decision.Should().Be("approve");
        a.Violations.Should().BeEmpty();
        a.RiskTier.Should().Be(RiskTier.None);
        a.RiskScore.Should().Be(0);
    }

    [Fact]
    public async Task Evaluation_is_scoped_to_the_requested_task()
    {
        // Two tasks: one read-only, one prod-deploy. A reject rule on
        // noProductionDeploy must only fire for the prod task — the
        // read-only task must come back clean.
        var readOnly = ATask(tools: new[] { "read-file" }, env: "dev");
        var prod = ATask(tools: new[] { "deploy" }, env: "prod");
        var view = AView(tasks: new[] { readOnly, prod }, tier: "sandbox");

        var rules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "reject", "requireNone": ["noProductionDeploy"] }
            ] } }
        """;

        await using var db = InMemoryDbFixture.Create();
        var version = SeedPolicyVersion(db, "no-prod", rules, Severity.Critical);

        var (eval, _) = NewEvaluator(
            db: db,
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(new[] { Effective(version, "no-prod") }));

        var prodResult = await eval.EvaluateTaskAsync(view.GoalId, prod.TaskId);
        var roResult = await eval.EvaluateTaskAsync(view.GoalId, readOnly.TaskId);

        prodResult!.Assessment.Decision.Should().Be("reject");
        prodResult.Assessment.Violations.Should().ContainSingle();

        roResult!.Assessment.Decision.Should().Be("manual",
            "the read-only task does not deploy to prod, so the reject rule must not fire for it");
        roResult.Assessment.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Caches_per_goal_task_and_plan_version()
    {
        var task = ATask();
        var view = AView(tasks: new[] { task });
        var stub = new StubTasksClient(_ => view);
        var (eval, _) = NewEvaluator(
            tasks: stub,
            bindings: new StubBindings(Array.Empty<EffectivePolicyDto>()));

        var first = await eval.EvaluateTaskAsync(view.GoalId, task.TaskId);
        var second = await eval.EvaluateTaskAsync(view.GoalId, task.TaskId);

        first.Should().NotBeNull();
        second.Should().BeSameAs(first,
            "the per-task cache must return the same response instance within the TTL");
    }

    [Fact]
    public async Task Replan_new_plan_version_busts_the_per_task_cache()
    {
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var calls = 0;
        var stub = new StubTasksClient(_ =>
        {
            calls++;
            return AView(goalId: goalId, planVersion: calls.ToString(),
                tasks: new[] { ATask(taskId: taskId) });
        });

        var (eval, _) = NewEvaluator(
            tasks: stub,
            bindings: new StubBindings(Array.Empty<EffectivePolicyDto>()));

        var first = await eval.EvaluateTaskAsync(goalId, taskId);
        var second = await eval.EvaluateTaskAsync(goalId, taskId);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Should().NotBeSameAs(second,
            "different plan versions must produce distinct per-task cache entries");
    }

    [Fact]
    public async Task Different_tasks_in_same_goal_are_cached_independently()
    {
        var t1 = ATask(env: "prod");
        var t2 = ATask(tools: new[] { "read-file" }, env: "dev");
        var view = AView(tasks: new[] { t1, t2 }, tier: "sandbox");

        var rules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "reject", "requireNone": ["noProductionDeploy"] }
            ] } }
        """;
        await using var db = InMemoryDbFixture.Create();
        var version = SeedPolicyVersion(db, "no-prod", rules, Severity.Critical);

        var (eval, _) = NewEvaluator(
            db: db,
            tasks: new StubTasksClient(_ => view),
            bindings: new StubBindings(new[] { Effective(version, "no-prod") }));

        var r1 = await eval.EvaluateTaskAsync(view.GoalId, t1.TaskId);
        var r2 = await eval.EvaluateTaskAsync(view.GoalId, t2.TaskId);

        r1!.Assessment.Decision.Should().Be("reject");
        r2!.Assessment.Decision.Should().Be("manual");
        r1.Should().NotBeSameAs(r2);
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
            (predicates ?? AllPredicates), new ComplianceScorer(),
            new NoopAuditPublisher(),
            cache, NullLogger<PlanEvaluator>.Instance);
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
        Guid? taskId = null,
        IEnumerable<string>? tools = null,
        string? agentId = "coder",
        string? env = null) => new(
        TaskId: taskId ?? Guid.NewGuid(),
        EffectiveAgentId: agentId,
        ToolsAllowed: tools?.ToList() ?? new List<string> { "read-file" },
        TargetEnv: env);

    private static PolicyVersion SeedPolicyVersion(
        Andy.Policies.Infrastructure.Data.AppDbContext db, string policyKey,
        string rulesJson, Severity severity)
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
            Severity = severity,
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
        public StubTasksClient(Func<Guid, PlanEvaluationGoalView?> project) => _project = project;

        public Task<PlanEvaluationGoalView?> FetchGoalViewAsync(Guid goalId, CancellationToken ct = default)
            => Task.FromResult(_project(goalId));
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

    private sealed class NoopAuditPublisher : IComplianceAuditPublisher
    {
        public Task<ComplianceAuditPublishResult> PublishPlanAsync(
            Guid goalId, string planVersion, ComplianceAssessment assessment, CancellationToken ct = default)
            => Task.FromResult(ComplianceAuditPublishResult.SkippedResult);

        public Task<ComplianceAuditPublishResult> PublishTaskAsync(
            Guid goalId, Guid taskId, string planVersion, ComplianceAssessment assessment, CancellationToken ct = default)
            => Task.FromResult(ComplianceAuditPublishResult.SkippedResult);
    }
}
