// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Net.Http.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.PlanEvaluation;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.PlanEvaluation;

/// <summary>
/// Integration test for the andy-docs compliance/audit injection
/// (rivoli-ai/conductor#1945 — TX F7.2). Drives <c>evaluate-task</c> and
/// <c>evaluate-plan</c> through the real DI pipeline with
/// <c>ComplianceAudit:Enabled=true</c>, substituting only
/// <see cref="IDocsClient"/> with a capturing stub (so no real andy-docs
/// is required). Asserts the full chain
/// (controller → PlanEvaluator → ComplianceAuditPublisher → IDocsClient)
/// requests one assessment document + one audit-chain segment with the
/// right <c>role:Audit</c> link tuple, that the returned DocsRef is
/// produced, and that the 5-minute idempotency cache prevents a re-eval
/// from re-publishing (the andy-docs attach is itself idempotent, but the
/// cache hit means andy-policies doesn't even call out a second time).
/// </summary>
public sealed class ComplianceAuditInjectionTests
    : IClassFixture<ComplianceAuditInjectionFactory>
{
    private readonly ComplianceAuditInjectionFactory _factory;
    private readonly HttpClient _client;

    public ComplianceAuditInjectionTests(ComplianceAuditInjectionFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Task_eval_injects_assessment_and_chain_segment_linked_to_goal_and_task()
    {
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var view = new PlanEvaluationGoalView(
            GoalId: goalId,
            PlanVersion: "1",
            WorkspaceContainerId: $"ctr-{Guid.NewGuid():N}",
            WorkspaceTier: "sandbox",
            WorkspaceApprovedAgentIds: new[] { "coder" },
            WorkspaceCostBudgetUsd: 10m,
            Tasks: new List<PlanEvaluationTaskView>
            {
                new(taskId, "coder", new[] { "read-file" }, null),
            },
            EstimatedCostUsd: 1m);
        _factory.TasksStub.Add(goalId, view);
        _factory.DocsStub.Clear();

        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task", new EvaluateTaskRequest(goalId, taskId));
        resp.EnsureSuccessStatusCode();

        _factory.DocsStub.Requests.Should().HaveCount(2,
            "one role:Audit assessment doc + one tamper-evident audit-chain segment");

        var assessment = _factory.DocsStub.Requests[0];
        assessment.MimeType.Should().Be("application/json");
        assessment.Links.Should().HaveCount(2);
        assessment.Links.Should().Contain(l =>
            l.TargetType == DocsLinkTargetType.Goal &&
            l.TargetId == goalId.ToString("D") && l.Role == "Audit");
        assessment.Links.Should().Contain(l =>
            l.TargetType == DocsLinkTargetType.Task &&
            l.TargetId == taskId.ToString("D") && l.Role == "Audit");

        _factory.DocsStub.Requests[1].FileName.Should().EndWith(".ndjson");
    }

    [Fact]
    public async Task Plan_eval_injects_assessment_linked_to_goal_only()
    {
        var goalId = Guid.NewGuid();
        var view = new PlanEvaluationGoalView(
            GoalId: goalId,
            PlanVersion: "1",
            WorkspaceContainerId: $"ctr-{Guid.NewGuid():N}",
            WorkspaceTier: "sandbox",
            WorkspaceApprovedAgentIds: new[] { "coder" },
            WorkspaceCostBudgetUsd: 10m,
            Tasks: new List<PlanEvaluationTaskView>
            {
                new(Guid.NewGuid(), "coder", new[] { "read-file" }, null),
            },
            EstimatedCostUsd: 1m);
        _factory.TasksStub.Add(goalId, view);
        _factory.DocsStub.Clear();

        var resp = await _client.PostAsJsonAsync(
            "/api/policies/evaluate-plan", new EvaluatePlanRequest(goalId));
        resp.EnsureSuccessStatusCode();

        _factory.DocsStub.Requests.Should().NotBeEmpty();
        var assessment = _factory.DocsStub.Requests[0];
        assessment.Links.Should().OnlyContain(l =>
            l.TargetType == DocsLinkTargetType.Goal && l.Role == "Audit");
        assessment.Links.Should().ContainSingle(l => l.TargetId == goalId.ToString("D"));
    }

    [Fact]
    public async Task Idempotency_cache_prevents_re_publishing_on_repeat_eval()
    {
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var view = new PlanEvaluationGoalView(
            GoalId: goalId,
            PlanVersion: "1",
            WorkspaceContainerId: $"ctr-{Guid.NewGuid():N}",
            WorkspaceTier: "sandbox",
            WorkspaceApprovedAgentIds: new[] { "coder" },
            WorkspaceCostBudgetUsd: 10m,
            Tasks: new List<PlanEvaluationTaskView>
            {
                new(taskId, "coder", new[] { "read-file" }, null),
            },
            EstimatedCostUsd: 1m);
        _factory.TasksStub.Add(goalId, view);
        _factory.DocsStub.Clear();

        await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task", new EvaluateTaskRequest(goalId, taskId));
        var afterFirst = _factory.DocsStub.Requests.Count;

        await _client.PostAsJsonAsync(
            "/api/policies/evaluate-task", new EvaluateTaskRequest(goalId, taskId));
        var afterSecond = _factory.DocsStub.Requests.Count;

        afterFirst.Should().Be(2);
        afterSecond.Should().Be(2, "the cache hit short-circuits before publishing again");
    }
}

/// <summary>
/// Factory mirroring <see cref="EvaluateTaskFactory"/> but with
/// <c>ComplianceAudit:Enabled=true</c> and a capturing
/// <see cref="IDocsClient"/> so the injection path runs end-to-end
/// without a real andy-docs.
/// </summary>
public sealed class ComplianceAuditInjectionFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    public StubTasksPlanClient TasksStub { get; } = new();
    public CapturingDocsClient DocsStub { get; } = new();

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public ComplianceAuditInjectionFactory() => _connection.Open();

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
                ["AndyDocs:BaseUrl"] = "https://test-docs.invalid",
                ["ComplianceAudit:Enabled"] = "true",
                ["ComplianceAudit:IncludeAuditChainSegment"] = "true",
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

            var rbacDescriptors = services
                .Where(d => d.ServiceType == typeof(IRbacChecker)).ToList();
            foreach (var d in rbacDescriptors) services.Remove(d);
            services.AddSingleton<IRbacChecker>(new AllowAllRbacChecker());

            var pinDescriptors = services
                .Where(d => d.ServiceType == typeof(IPinningPolicy)).ToList();
            foreach (var d in pinDescriptors) services.Remove(d);
            services.AddSingleton<IPinningPolicy>(new StaticPinning());

            var ratDescriptors = services
                .Where(d => d.ServiceType == typeof(IRationalePolicy)).ToList();
            foreach (var d in ratDescriptors) services.Remove(d);
            services.AddSingleton<IRationalePolicy>(new StaticRationale());

            var clientDescriptors = services
                .Where(d => d.ServiceType == typeof(ITasksPlanClient)).ToList();
            foreach (var d in clientDescriptors) services.Remove(d);
            services.AddSingleton<ITasksPlanClient>(TasksStub);

            // Substitute the andy-docs client with a capturing stub.
            var docsDescriptors = services
                .Where(d => d.ServiceType == typeof(IDocsClient)).ToList();
            foreach (var d in docsDescriptors) services.Remove(d);
            services.AddSingleton<IDocsClient>(DocsStub);

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

    private sealed class StaticPinning : IPinningPolicy
    {
        public bool IsPinningRequired => false;
    }

    private sealed class StaticRationale : IRationalePolicy
    {
        public bool IsRequired => false;
        public string? ValidateRationale(string? rationale) => null;
    }

    public sealed class CapturingDocsClient : IDocsClient
    {
        private readonly ConcurrentQueue<DocsPutRequest> _requests = new();

        public IReadOnlyList<DocsPutRequest> Requests => _requests.ToArray();

        public void Clear()
        {
            while (_requests.TryDequeue(out _))
            {
            }
        }

        public Task<DocsRef> PutAsync(DocsPutRequest request, CancellationToken ct = default)
        {
            _requests.Enqueue(request);
            return Task.FromResult(new DocsRef(
                Guid.NewGuid(), Guid.NewGuid(), "version-hash", request.Content.LongLength,
                Array.Empty<Guid>()));
        }
    }
}
