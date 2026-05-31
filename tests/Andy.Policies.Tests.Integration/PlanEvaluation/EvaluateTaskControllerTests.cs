// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.PlanEvaluation;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services.PlanEvaluation;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.PlanEvaluation;

/// <summary>
/// Integration test for <c>POST /api/policies/evaluate-task</c>
/// (rivoli-ai/conductor#1944 — TX F7.1). Drives the controller through
/// the real DI pipeline — SQLite-backed <see cref="AppDbContext"/>, the
/// real binding-resolution service, the real predicate registry, the
/// real <see cref="ComplianceScorer"/>, and the real
/// <see cref="PlanEvaluator"/>. Only <see cref="ITasksPlanClient"/> is
/// stubbed. Asserts the 200 structured assessment, the idempotency cache
/// (hit on repeat, bust on new planVersion), and the 400/404/403 failure
/// modes. The recorded 200 fixture is the contract reused by #1946's
/// URLProtocol stub (the E2E layer for this story).
/// </summary>
public sealed class EvaluateTaskControllerTests : IClassFixture<EvaluateTaskFactory>
{
    private readonly EvaluateTaskFactory _factory;
    private readonly HttpClient _client;

    public EvaluateTaskControllerTests(EvaluateTaskFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Returns_400_when_goalId_is_empty_guid()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task",
            new EvaluateTaskRequest(Guid.Empty, Guid.NewGuid()));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Returns_400_when_taskId_is_empty_guid()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task",
            new EvaluateTaskRequest(Guid.NewGuid(), Guid.Empty));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Returns_404_when_goal_is_unknown()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task",
            new EvaluateTaskRequest(Guid.NewGuid(), Guid.NewGuid()));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Returns_404_when_goal_does_not_contain_the_task()
    {
        var goalId = Guid.NewGuid();
        var view = AView(goalId, "ctr-x", taskId: Guid.NewGuid());
        _factory.TasksStub.Add(goalId, view);

        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task",
            new EvaluateTaskRequest(goalId, Guid.NewGuid())); // task not in goal

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Returns_200_with_structured_assessment_and_reject_violation()
    {
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var container = $"ctr-task-{Guid.NewGuid():N}";

        var view = new PlanEvaluationGoalView(
            GoalId: goalId,
            PlanVersion: "1",
            WorkspaceContainerId: container,
            WorkspaceTier: "production",
            WorkspaceApprovedAgentIds: new[] { "coder" },
            WorkspaceCostBudgetUsd: 100m,
            Tasks: new List<PlanEvaluationTaskView>
            {
                new(taskId, "coder", new[] { "deploy" }, "prod"),
            },
            EstimatedCostUsd: 1m);
        _factory.TasksStub.Add(goalId, view);

        await SeedRejectPolicyAsync(container, "no-prod-deploy", Severity.Critical);

        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task", new EvaluateTaskRequest(goalId, taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<EvaluateTaskResponse>();
        body.Should().NotBeNull();
        body!.GoalId.Should().Be(goalId);
        body.TaskId.Should().Be(taskId);

        var a = body.Assessment;
        a.Decision.Should().Be("reject");
        a.RiskTier.Should().Be(RiskTier.Critical);
        a.RiskScore.Should().Be(20);
        a.Violations.Should().ContainSingle();
        a.Violations[0].PolicyKey.Should().Be("no-prod-deploy");
        a.Violations[0].Predicate.Should().Be(PlanPredicateNames.NoProductionDeploy);
        a.Violations[0].Outcome.Should().Be("fail");
        a.Predicates.Should().HaveCount(5);
    }

    [Fact]
    public async Task Returns_200_manual_with_no_violations_when_no_policy_bound()
    {
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var view = AView(goalId, "ctr-unbound-task", taskId);
        _factory.TasksStub.Add(goalId, view);

        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task", new EvaluateTaskRequest(goalId, taskId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<EvaluateTaskResponse>();
        body!.Assessment.Decision.Should().Be("manual");
        body.Assessment.RiskTier.Should().Be(RiskTier.None);
        body.Assessment.RiskScore.Should().Be(0);
        body.Assessment.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Idempotent_per_goal_task_and_plan_version()
    {
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var view = AView(goalId, "ctr-idem", taskId);
        _factory.TasksStub.Add(goalId, view);

        var first = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task", new EvaluateTaskRequest(goalId, taskId));
        var second = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task", new EvaluateTaskRequest(goalId, taskId));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var a1 = (await first.Content.ReadFromJsonAsync<EvaluateTaskResponse>())!.Assessment;
        var a2 = (await second.Content.ReadFromJsonAsync<EvaluateTaskResponse>())!.Assessment;
        // Cache hit returns the verbatim response, including the same
        // evaluatedAt timestamp.
        a2.EvaluatedAt.Should().Be(a1.EvaluatedAt);
    }

    [Fact]
    public async Task New_plan_version_busts_the_cache()
    {
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        _factory.TasksStub.Add(goalId, AView(goalId, "ctr-bust", taskId, planVersion: "1"));
        var first = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task", new EvaluateTaskRequest(goalId, taskId));
        var a1 = (await first.Content.ReadFromJsonAsync<EvaluateTaskResponse>())!.Assessment;

        // Replan: same goal+task, new plan version → cache must bust.
        _factory.TasksStub.Add(goalId, AView(goalId, "ctr-bust", taskId, planVersion: "2"));
        var second = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task", new EvaluateTaskRequest(goalId, taskId));
        var a2 = (await second.Content.ReadFromJsonAsync<EvaluateTaskResponse>())!.Assessment;

        a2.EvaluatedAt.Should().NotBe(a1.EvaluatedAt,
            "a new plan version must produce a fresh assessment, not a cached one");
    }

    [Fact]
    public async Task Returns_403_when_principal_lacks_the_evaluate_task_permission()
    {
        using var denyFactory = new DenyTaskPermissionFactory();
        var client = denyFactory.CreateClient();
        var goalId = Guid.NewGuid();
        denyFactory.TasksStub.Add(goalId, AView(goalId, "ctr-403", Guid.NewGuid()));

        var resp = await client.PostAsJsonAsync(
            "/api/policies/evaluate-task", new EvaluateTaskRequest(goalId, Guid.NewGuid()));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- helpers ----------------------------------------------------------

    private static PlanEvaluationGoalView AView(
        Guid goalId, string container, Guid taskId, string planVersion = "1") => new(
        GoalId: goalId,
        PlanVersion: planVersion,
        WorkspaceContainerId: container,
        WorkspaceTier: "sandbox",
        WorkspaceApprovedAgentIds: new[] { "coder" },
        WorkspaceCostBudgetUsd: 10m,
        Tasks: new List<PlanEvaluationTaskView>
        {
            new(taskId, "coder", new[] { "read-file" }, null),
        },
        EstimatedCostUsd: 1m);

    private async Task SeedRejectPolicyAsync(string container, string policyKey, Severity severity)
    {
        var rules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "reject",
                "requireNone": ["noProductionDeploy"] }
            ] } }
        """;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = policyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "integration",
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
            RulesJson = rules,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "integration",
            ProposerSubjectId = "integration",
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedBySubjectId = "integration",
        };
        var binding = new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = version.Id,
            TargetType = BindingTargetType.ScopeNode,
            TargetRef = $"scope:{container}",
            BindStrength = BindStrength.Mandatory,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        db.Bindings.Add(binding);
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Factory for <see cref="EvaluateTaskControllerTests"/>. Mirrors
/// <see cref="EvaluatePlanFactory"/>: SQLite + TestAuth + allow-all RBAC +
/// an in-memory <see cref="StubTasksPlanClient"/>.
/// </summary>
public sealed class EvaluateTaskFactory : WebApplicationFactory<Program>
{
    public StubTasksPlanClient TasksStub { get; } = new();

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public EvaluateTaskFactory() => _connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["AndyAuth:Authority"] = "https://test-auth.invalid",
                ["AndySettings:ApiBaseUrl"] = "https://test-settings.invalid",
                ["AndyRbac:BaseUrl"] = "https://test-rbac.invalid",
                ["AndyTasks:BaseUrl"] = "https://test-tasks.invalid",
            });
        });

        builder.ConfigureServices(services =>
        {
            var ctxDescriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (ctxDescriptor is not null) services.Remove(ctxDescriptor);
            services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(opts =>
            {
                opts.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
                        TestAuthHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .Build();
            });

            var rbacDescriptors = services
                .Where(d => d.ServiceType == typeof(IRbacChecker))
                .ToList();
            foreach (var d in rbacDescriptors) services.Remove(d);
            services.AddSingleton<IRbacChecker>(new AllowAllRbacChecker());

            var pinDescriptors = services
                .Where(d => d.ServiceType == typeof(Andy.Policies.Application.Interfaces.IPinningPolicy))
                .ToList();
            foreach (var d in pinDescriptors) services.Remove(d);
            services.AddSingleton<Andy.Policies.Application.Interfaces.IPinningPolicy>(
                new StaticPinning());

            var ratDescriptors = services
                .Where(d => d.ServiceType == typeof(Andy.Policies.Application.Interfaces.IRationalePolicy))
                .ToList();
            foreach (var d in ratDescriptors) services.Remove(d);
            services.AddSingleton<Andy.Policies.Application.Interfaces.IRationalePolicy>(
                new StaticRationale());

            var clientDescriptors = services
                .Where(d => d.ServiceType == typeof(ITasksPlanClient))
                .ToList();
            foreach (var d in clientDescriptors) services.Remove(d);
            services.AddSingleton<ITasksPlanClient>(TasksStub);

            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _connection.Dispose();
        base.Dispose(disposing);
    }

    private sealed class AllowAllRbacChecker : IRbacChecker
    {
        public Task<RbacDecision> CheckAsync(
            string subjectId, string permissionCode, IReadOnlyList<string> groups,
            string? resourceInstanceId, CancellationToken ct)
            => Task.FromResult(new RbacDecision(true, "test-allow"));
    }

    private sealed class StaticPinning : Andy.Policies.Application.Interfaces.IPinningPolicy
    {
        public bool IsPinningRequired => false;
    }

    private sealed class StaticRationale : Andy.Policies.Application.Interfaces.IRationalePolicy
    {
        public bool IsRequired => false;
        public string? ValidateRationale(string? rationale) => null;
    }
}

/// <summary>
/// Variant of <see cref="EvaluateTaskFactory"/> whose RBAC checker denies
/// the <c>andy-policies:plan:evaluate-task</c> permission so the
/// <c>[Authorize(Policy=...)]</c> gate yields 403.
/// </summary>
public sealed class DenyTaskPermissionFactory : WebApplicationFactory<Program>
{
    public StubTasksPlanClient TasksStub { get; } = new();
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public DenyTaskPermissionFactory() => _connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["AndyAuth:Authority"] = "https://test-auth.invalid",
                ["AndySettings:ApiBaseUrl"] = "https://test-settings.invalid",
                ["AndyRbac:BaseUrl"] = "https://test-rbac.invalid",
                ["AndyTasks:BaseUrl"] = "https://test-tasks.invalid",
            });
        });

        builder.ConfigureServices(services =>
        {
            var ctxDescriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (ctxDescriptor is not null) services.Remove(ctxDescriptor);
            services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(opts =>
            {
                opts.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
                        TestAuthHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .Build();
            });

            var rbacDescriptors = services
                .Where(d => d.ServiceType == typeof(IRbacChecker))
                .ToList();
            foreach (var d in rbacDescriptors) services.Remove(d);
            services.AddSingleton<IRbacChecker>(new DenyTaskChecker());

            var clientDescriptors = services
                .Where(d => d.ServiceType == typeof(ITasksPlanClient))
                .ToList();
            foreach (var d in clientDescriptors) services.Remove(d);
            services.AddSingleton<ITasksPlanClient>(TasksStub);

            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _connection.Dispose();
        base.Dispose(disposing);
    }

    private sealed class DenyTaskChecker : IRbacChecker
    {
        public Task<RbacDecision> CheckAsync(
            string subjectId, string permissionCode, IReadOnlyList<string> groups,
            string? resourceInstanceId, CancellationToken ct)
        {
            var allow = permissionCode != "andy-policies:plan:evaluate-task";
            return Task.FromResult(new RbacDecision(allow, allow ? "allow" : "deny-task-eval"));
        }
    }
}
