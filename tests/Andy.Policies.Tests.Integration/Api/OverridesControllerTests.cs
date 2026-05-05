// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.Settings;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Andy.Policies.Infrastructure.Data;
using Xunit;

namespace Andy.Policies.Tests.Integration.Api;

/// <summary>
/// P5.5 (#58) — exercises the six override REST endpoints end-to-end
/// against the SQLite-backed factory. Stands up a parallel
/// <see cref="OverridesFactory"/> that swaps
/// <see cref="IExperimentalOverridesGate"/> for a controllable stub
/// so the gate can be flipped per-test without re-spinning the host.
/// </summary>
public class OverridesControllerTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class StubGate : IExperimentalOverridesGate
    {
        public bool IsEnabled { get; set; } = true;
    }

    private sealed class OverridesFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public StubGate Gate { get; } = new();

        public OverridesFactory()
        {
            _connection.Open();
        }

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
                });
            });

            builder.ConfigureServices(services =>
            {
                var ctxDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (ctxDescriptor is not null) services.Remove(ctxDescriptor);
                services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

                var gateDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(IExperimentalOverridesGate));
                if (gateDescriptor is not null) services.Remove(gateDescriptor);
                services.AddSingleton<IExperimentalOverridesGate>(Gate);

                // P7.2 (#51): swap the production HttpRbacChecker for an
                // allow-all stub so RBAC-gated endpoints (approve / revoke)
                // don't fail-closed deny on unreachable test-rbac.invalid.
                var rbacDescriptors = services
                    .Where(d => d.ServiceType == typeof(IRbacChecker))
                    .ToList();
                foreach (var d in rbacDescriptors) services.Remove(d);
                services.AddSingleton<IRbacChecker>(new AllowAllStubRbacChecker());

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
                services.PostConfigure<AuthorizationOptions>(opts =>
                {
                    opts.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .Build();
                });

                using var sp = services.BuildServiceProvider();
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _connection.Dispose();
            base.Dispose(disposing);
        }

        private sealed class AllowAllStubRbacChecker : IRbacChecker
        {
            public Task<RbacDecision> CheckAsync(
                string subjectId, string permissionCode, IReadOnlyList<string> groups,
                string? resourceInstanceId, CancellationToken ct)
                => Task.FromResult(new RbacDecision(true, "test-allow"));
        }
    }

    private readonly OverridesFactory _factory = new();
    private HttpClient Client => _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<PolicyVersionDto> CreateActivePolicyVersionAsync(HttpClient client, string slug)
    {
        var create = new CreatePolicyRequest(
            Name: slug,
            Description: null,
            Summary: "summary",
            Enforcement: "Must",
            Severity: "Critical",
            Scopes: Array.Empty<string>(),
            RulesJson: "{}");
        var draftResp = await client.PostAsJsonAsync("/api/policies", create);
        draftResp.EnsureSuccessStatusCode();
        var draft = (await draftResp.Content.ReadFromJsonAsync<PolicyVersionDto>(JsonOptions))!;

        var publishResp = await client.PostAsJsonAsApproverAsync(
            $"/api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest("go-live"));
        publishResp.EnsureSuccessStatusCode();
        return (await publishResp.Content.ReadFromJsonAsync<PolicyVersionDto>(JsonOptions))!;
    }

    private static ProposeOverrideRequest ExemptRequest(
        Guid policyVersionId,
        string scopeRef = "user:42",
        DateTimeOffset? expiresAt = null,
        string rationale = "expedite review for vendor-blocked story")
        => new(
            PolicyVersionId: policyVersionId,
            ScopeKind: OverrideScopeKind.Principal,
            ScopeRef: scopeRef,
            Effect: OverrideEffect.Exempt,
            ReplacementPolicyVersionId: null,
            ExpiresAt: expiresAt ?? DateTimeOffset.UtcNow.AddHours(24),
            Rationale: rationale);

    private static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}".Substring(0, 16);

    private static HttpRequestMessage SignedAs(HttpMethod method, string path, string subject, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(TestAuthHandler.SubjectHeader, subject);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        return req;
    }

    [Fact]
    public async Task Propose_HappyPath_Returns201_WithLocationHeader()
    {
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-create"));

        var resp = await client.PostAsJsonAsync("/api/overrides", ExemptRequest(version.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        resp.Headers.Location.Should().NotBeNull();
        resp.Headers.Location!.AbsolutePath.Should().StartWith("/api/overrides/");
        var dto = await resp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions);
        dto!.State.Should().Be(OverrideState.Proposed);
        dto.PolicyVersionId.Should().Be(version.Id);
    }

    [Fact]
    public async Task Propose_GateOff_Returns403WithErrorCode()
    {
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-gate"));
        _factory.Gate.IsEnabled = false;

        var resp = await client.PostAsJsonAsync("/api/overrides", ExemptRequest(version.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        problem.GetProperty("type").GetString().Should().Be("/problems/override-disabled");
        problem.GetProperty("errorCode").GetString().Should().Be("override.disabled");

        _factory.Gate.IsEnabled = true; // restore for downstream tests
    }

    [Fact]
    public async Task Propose_BlankRationale_Returns400()
    {
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-rationale"));
        var bad = ExemptRequest(version.Id, rationale: "   ");

        var resp = await client.PostAsJsonAsync("/api/overrides", bad);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Approve_BySomeoneOtherThanProposer_Returns200_AndTransitions()
    {
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-app-ok"));

        // Propose as user A.
        var proposeResp = await client.SendAsync(
            SignedAs(HttpMethod.Post, "/api/overrides", "user:proposer", ExemptRequest(version.Id)));
        proposeResp.EnsureSuccessStatusCode();
        var proposed = (await proposeResp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions))!;

        // Approve as user B.
        var approveResp = await client.SendAsync(
            SignedAs(HttpMethod.Post, $"/api/overrides/{proposed.Id}/approve", "user:approver"));

        approveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var approved = await approveResp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions);
        approved!.State.Should().Be(OverrideState.Approved);
        approved.ApproverSubjectId.Should().Be("user:approver");
    }

    [Fact]
    public async Task Approve_ByProposer_Returns403_WithSelfApprovalErrorCode()
    {
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-self"));

        var proposeResp = await client.SendAsync(
            SignedAs(HttpMethod.Post, "/api/overrides", "user:proposer", ExemptRequest(version.Id)));
        proposeResp.EnsureSuccessStatusCode();
        var proposed = (await proposeResp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions))!;

        var approveResp = await client.SendAsync(
            SignedAs(HttpMethod.Post, $"/api/overrides/{proposed.Id}/approve", "user:proposer"));

        approveResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await approveResp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        problem.GetProperty("type").GetString().Should().Be("/problems/override-self-approval");
        problem.GetProperty("errorCode").GetString().Should().Be("override.self_approval_forbidden");
    }

    [Fact]
    public async Task Approve_AlreadyApproved_Returns409()
    {
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-9"));

        var proposeResp = await client.SendAsync(
            SignedAs(HttpMethod.Post, "/api/overrides", "user:p1", ExemptRequest(version.Id)));
        proposeResp.EnsureSuccessStatusCode();
        var proposed = (await proposeResp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions))!;

        var firstApprove = await client.SendAsync(
            SignedAs(HttpMethod.Post, $"/api/overrides/{proposed.Id}/approve", "user:p2"));
        firstApprove.EnsureSuccessStatusCode();

        var secondApprove = await client.SendAsync(
            SignedAs(HttpMethod.Post, $"/api/overrides/{proposed.Id}/approve", "user:p3"));
        secondApprove.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Approve_UnknownId_Returns404()
    {
        var client = Client;

        var resp = await client.PostAsync($"/api/overrides/{Guid.NewGuid()}/approve", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Revoke_BlankReason_Returns400()
    {
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-rev0"));
        var proposeResp = await client.PostAsJsonAsync("/api/overrides", ExemptRequest(version.Id));
        proposeResp.EnsureSuccessStatusCode();
        var proposed = (await proposeResp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions))!;

        var resp = await client.PostAsJsonAsync(
            $"/api/overrides/{proposed.Id}/revoke", new RevokeOverrideRequest("   "));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Revoke_HappyPath_FromProposed_Returns200()
    {
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-rev1"));
        var proposeResp = await client.PostAsJsonAsync("/api/overrides", ExemptRequest(version.Id));
        proposeResp.EnsureSuccessStatusCode();
        var proposed = (await proposeResp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions))!;

        var resp = await client.PostAsJsonAsync(
            $"/api/overrides/{proposed.Id}/revoke", new RevokeOverrideRequest("withdrawn"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions);
        dto!.State.Should().Be(OverrideState.Revoked);
        dto.RevocationReason.Should().Be("withdrawn");
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        var client = Client;

        var resp = await client.GetAsync($"/api/overrides/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_ExistingId_Returns200()
    {
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-get"));
        var proposeResp = await client.PostAsJsonAsync("/api/overrides", ExemptRequest(version.Id));
        proposeResp.EnsureSuccessStatusCode();
        var proposed = (await proposeResp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions))!;

        var resp = await client.GetAsync($"/api/overrides/{proposed.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions);
        dto!.Id.Should().Be(proposed.Id);
    }

    [Fact]
    public async Task List_FiltersByState()
    {
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-list"));
        await client.PostAsJsonAsync("/api/overrides", ExemptRequest(version.Id, "user:1"));
        var proposeB = await client.SendAsync(
            SignedAs(HttpMethod.Post, "/api/overrides", "user:proposer-b",
                ExemptRequest(version.Id, "user:2")));
        var proposedB = (await proposeB.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions))!;
        await client.SendAsync(
            SignedAs(HttpMethod.Post, $"/api/overrides/{proposedB.Id}/approve", "user:other"));

        var resp = await client.GetAsync("/api/overrides?state=Approved");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dtos = await resp.Content.ReadFromJsonAsync<List<OverrideDto>>(JsonOptions);
        dtos.Should().ContainSingle().Which.Id.Should().Be(proposedB.Id);
    }

    [Fact]
    public async Task Active_RequiresBothQueryParams()
    {
        var client = Client;

        var noKind = await client.GetAsync("/api/overrides/active?scopeRef=user:42");
        var noRef = await client.GetAsync("/api/overrides/active?scopeKind=Principal");

        noKind.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noRef.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Active_FiltersToApprovedAndNonExpired()
    {
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-act"));

        // Approved + far-future expiry: should appear.
        var liveResp = await client.SendAsync(
            SignedAs(HttpMethod.Post, "/api/overrides", "user:p1",
                ExemptRequest(version.Id, "user:42",
                    expiresAt: DateTimeOffset.UtcNow.AddHours(24))));
        var live = (await liveResp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions))!;
        await client.SendAsync(
            SignedAs(HttpMethod.Post, $"/api/overrides/{live.Id}/approve", "user:approver"));

        // Proposed but never approved: should NOT appear.
        await client.SendAsync(
            SignedAs(HttpMethod.Post, "/api/overrides", "user:p2",
                ExemptRequest(version.Id, "user:42")));

        // Different scope (user:99): should NOT appear.
        var otherResp = await client.SendAsync(
            SignedAs(HttpMethod.Post, "/api/overrides", "user:p3",
                ExemptRequest(version.Id, "user:99")));
        var other = (await otherResp.Content.ReadFromJsonAsync<OverrideDto>(JsonOptions))!;
        await client.SendAsync(
            SignedAs(HttpMethod.Post, $"/api/overrides/{other.Id}/approve", "user:approver"));

        var resp = await client.GetAsync("/api/overrides/active?scopeKind=Principal&scopeRef=user:42");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dtos = await resp.Content.ReadFromJsonAsync<List<OverrideDto>>(JsonOptions);
        dtos.Should().ContainSingle().Which.Id.Should().Be(live.Id);
    }

    [Fact]
    public async Task ReadEndpoints_BypassGate()
    {
        // Settings gate off; reads must still respond 200 so the
        // resolution algorithm (P4.3) keeps working when the toggle
        // is flipped off — turning the feature off must not strand
        // existing approved overrides for consumers.
        var client = Client;
        var version = await CreateActivePolicyVersionAsync(client, Slug("ovr-read-gate"));
        var proposeResp = await client.PostAsJsonAsync("/api/overrides", ExemptRequest(version.Id));
        proposeResp.EnsureSuccessStatusCode();

        _factory.Gate.IsEnabled = false;
        try
        {
            (await client.GetAsync("/api/overrides")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync("/api/overrides/active?scopeKind=Principal&scopeRef=user:42"))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            _factory.Gate.IsEnabled = true;
        }
    }
}
