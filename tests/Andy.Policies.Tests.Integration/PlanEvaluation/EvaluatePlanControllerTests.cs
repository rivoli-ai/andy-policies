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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.PlanEvaluation;

/// <summary>
/// End-to-end integration test for the
/// <c>POST /api/policies/evaluate-plan</c> surface
/// (rivoli-ai/andy-policies#232). Drives the controller through the
/// real DI pipeline — including the test SQLite-backed
/// <see cref="AppDbContext"/>, the real binding resolution service,
/// the real predicate registry, and the real
/// <see cref="PlanEvaluator"/>. Only <see cref="ITasksPlanClient"/> is
/// stubbed (otherwise the test would need a live andy-tasks).
/// </summary>
public sealed class EvaluatePlanControllerTests : IClassFixture<EvaluatePlanFactory>
{
    private readonly EvaluatePlanFactory _factory;
    private readonly HttpClient _client;

    public EvaluatePlanControllerTests(EvaluatePlanFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Returns_400_when_goalId_is_empty_guid()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-plan",
            new EvaluatePlanRequest(Guid.Empty));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Returns_404_when_andy_tasks_does_not_recognise_the_goal()
    {
        var goalId = Guid.NewGuid();
        // Default stub: returns null for unknown goals.

        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-plan", new EvaluatePlanRequest(goalId));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Returns_manual_when_no_policies_are_bound_to_workspace()
    {
        var goalId = Guid.NewGuid();
        _factory.TasksStub.Add(goalId, AReadOnlyPlan(goalId, "ctr-unbound"));

        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-plan", new EvaluatePlanRequest(goalId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<EvaluatePlanResponse>();
        body!.Decision.Should().Be("manual");
        body.PolicyId.Should().BeNull();
        body.Predicates.Should().HaveCount(5);
    }

    [Fact]
    public async Task Returns_approve_when_workspace_policy_rule_fires()
    {
        var goalId = Guid.NewGuid();
        var container = $"ctr-approve-{Guid.NewGuid():N}";

        var view = new PlanEvaluationGoalView(
            GoalId: goalId,
            PlanVersion: "1",
            WorkspaceContainerId: container,
            WorkspaceTier: "sandbox",
            WorkspaceApprovedAgentIds: new[] { "coder" },
            WorkspaceCostBudgetUsd: 100m,
            Tasks: new List<PlanEvaluationTaskView>
            {
                new(Guid.NewGuid(), "coder", new[] { "read-file" }, null),
            },
            EstimatedCostUsd: 1m);
        _factory.TasksStub.Add(goalId, view);

        // Seed a published policy + binding that targets the
        // workspace's container ID via the scope:{guid} canonical ref.
        await SeedApprovePolicyAsync(container, policyKey: "sandbox-auto-approve");

        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-plan", new EvaluatePlanRequest(goalId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<EvaluatePlanResponse>();
        body!.Decision.Should().Be("approve");
        body.PolicyId.Should().Be("sandbox-auto-approve");
    }

    [Fact]
    public async Task Returns_reject_when_workspace_policy_negative_rule_fires()
    {
        var goalId = Guid.NewGuid();
        var container = $"ctr-reject-{Guid.NewGuid():N}";

        var view = new PlanEvaluationGoalView(
            GoalId: goalId,
            PlanVersion: "1",
            WorkspaceContainerId: container,
            WorkspaceTier: "production",
            WorkspaceApprovedAgentIds: new[] { "coder" },
            WorkspaceCostBudgetUsd: 100m,
            Tasks: new List<PlanEvaluationTaskView>
            {
                new(Guid.NewGuid(), "coder", new[] { "write-file" }, null),
            },
            EstimatedCostUsd: 1m);
        _factory.TasksStub.Add(goalId, view);

        await SeedRejectPolicyAsync(container, policyKey: "no-writes");

        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-plan", new EvaluatePlanRequest(goalId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<EvaluatePlanResponse>();
        body!.Decision.Should().Be("reject");
        body.PolicyId.Should().Be("no-writes");
    }

    [Fact]
    public async Task Predicate_trace_carries_data_unavailable_for_missing_inputs()
    {
        var goalId = Guid.NewGuid();
        var view = new PlanEvaluationGoalView(
            GoalId: goalId,
            PlanVersion: "1",
            WorkspaceContainerId: "ctr-x",
            WorkspaceTier: null,                 // → workspaceIsSandbox unevaluable
            WorkspaceApprovedAgentIds: null,     // → allAgentsApproved unevaluable
            WorkspaceCostBudgetUsd: null,        // → respectsCostBudget unevaluable
            Tasks: new List<PlanEvaluationTaskView>
            {
                new(Guid.NewGuid(), "coder", new[] { "read-file" }, null),
            },
            EstimatedCostUsd: null);
        _factory.TasksStub.Add(goalId, view);

        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-plan", new EvaluatePlanRequest(goalId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<EvaluatePlanResponse>();
        body!.Predicates.Single(p => p.Name == PlanPredicateNames.WorkspaceIsSandbox)
            .Reason.Should().Be("data unavailable");
        body.Predicates.Single(p => p.Name == PlanPredicateNames.AllAgentsApproved)
            .Reason.Should().Be("data unavailable");
        body.Predicates.Single(p => p.Name == PlanPredicateNames.RespectsCostBudget)
            .Reason.Should().Be("data unavailable");
    }

    // ---- helpers ----------------------------------------------------------

    private static PlanEvaluationGoalView AReadOnlyPlan(Guid goalId, string container) => new(
        GoalId: goalId,
        PlanVersion: "1",
        WorkspaceContainerId: container,
        WorkspaceTier: "sandbox",
        WorkspaceApprovedAgentIds: new[] { "coder" },
        WorkspaceCostBudgetUsd: 10m,
        Tasks: new List<PlanEvaluationTaskView>
        {
            new(Guid.NewGuid(), "coder", new[] { "read-file" }, null),
        },
        EstimatedCostUsd: 1m);

    private async Task SeedApprovePolicyAsync(string container, string policyKey)
    {
        var rules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "approve",
                "requireAll": ["allTasksReadOnly", "workspaceIsSandbox"] }
            ] } }
        """;
        await SeedPolicyAndBindingAsync(container, policyKey, rules);
    }

    private async Task SeedRejectPolicyAsync(string container, string policyKey)
    {
        var rules = """
        { "planEvaluation": {
            "decisionRules": [
              { "decision": "reject",
                "requireNone": ["allTasksReadOnly"] }
            ] } }
        """;
        await SeedPolicyAndBindingAsync(container, policyKey, rules);
    }

    private async Task SeedPolicyAndBindingAsync(string container, string policyKey, string rulesJson)
    {
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
            Severity = Severity.Moderate,
            Scopes = new List<string>(),
            Summary = "fixture",
            RulesJson = rulesJson,
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
/// Factory variant for <see cref="EvaluatePlanControllerTests"/>. Swaps
/// the registered <see cref="ITasksPlanClient"/> for an in-memory stub
/// (<see cref="StubTasksPlanClient"/>) so tests don't need a live
/// andy-tasks. Everything else mirrors <see cref="PoliciesApiFactory"/>'s
/// SQLite + TestAuth wiring so the test client can post against the
/// authenticated endpoint with no extra ceremony.
/// </summary>
public sealed class EvaluatePlanFactory : WebApplicationFactory<Program>
{
    public StubTasksPlanClient TasksStub { get; } = new();

    // Same lifetime pattern as PoliciesApiFactory: one shared connection
    // keeps the :memory: database alive for the factory's lifetime.
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public EvaluatePlanFactory() => _connection.Open();

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
            // EF Core 9+ registers several descriptors per context; leaving the Npgsql
            // ones in place makes EF 10 refuse two providers in one container.
            var dbDescriptors = services
                .Where(d => (d.ServiceType.IsGenericType
                             && d.ServiceType.GetGenericArguments().Contains(typeof(AppDbContext)))
                         || d.ServiceType == typeof(DbContextOptions)
                         || d.ServiceType == typeof(AppDbContext))
                .ToList();

            foreach (var descriptor in dbDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection)
                        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

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

            // Allow-all RBAC stub so the [Authorize(Policy = "andy-policies:plan:evaluate")]
            // gate doesn't 403 every test. The wiring of the policy itself is asserted
            // via Program.cs's rbacPermissionCodes list (manifest test).
            var rbacDescriptors = services
                .Where(d => d.ServiceType == typeof(IRbacChecker))
                .ToList();
            foreach (var d in rbacDescriptors) services.Remove(d);
            services.AddSingleton<IRbacChecker>(new AllowAllRbacChecker());

            // Pinning + rationale gates default-off (same posture as PoliciesApiFactory).
            var pinDescriptors = services
                .Where(d => d.ServiceType == typeof(Andy.Policies.Application.Interfaces.IPinningPolicy))
                .ToList();
            foreach (var d in pinDescriptors) services.Remove(d);
            services.AddSingleton<Andy.Policies.Application.Interfaces.IPinningPolicy>(
                new StaticPinning(required: false));

            var ratDescriptors = services
                .Where(d => d.ServiceType == typeof(Andy.Policies.Application.Interfaces.IRationalePolicy))
                .ToList();
            foreach (var d in ratDescriptors) services.Remove(d);
            services.AddSingleton<Andy.Policies.Application.Interfaces.IRationalePolicy>(
                new StaticRationale(required: false));

            // Swap the real HttpTasksPlanClient for the in-memory stub.
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
        public StaticPinning(bool required) => IsPinningRequired = required;
        public bool IsPinningRequired { get; }
    }

    private sealed class StaticRationale : Andy.Policies.Application.Interfaces.IRationalePolicy
    {
        public StaticRationale(bool required) => IsRequired = required;
        public bool IsRequired { get; }
        public string? ValidateRationale(string? rationale)
            => IsRequired && string.IsNullOrWhiteSpace(rationale)
                ? "Rationale is required for this operation."
                : null;
    }
}

/// <summary>
/// Stub <see cref="ITasksPlanClient"/> driven by the test (no HTTP).
/// Tests <see cref="Add"/> a goal view; the controller's call to
/// <see cref="FetchGoalViewAsync"/> looks it up by id. Unknown ids
/// return null, which the controller surfaces as 404.
/// </summary>
public sealed class StubTasksPlanClient : ITasksPlanClient
{
    private readonly Dictionary<Guid, PlanEvaluationGoalView> _byId = new();

    public void Add(Guid goalId, PlanEvaluationGoalView view) => _byId[goalId] = view;

    public Task<PlanEvaluationGoalView?> FetchGoalViewAsync(Guid goalId, CancellationToken ct = default)
        => Task.FromResult(_byId.TryGetValue(goalId, out var v) ? v : null);
}
