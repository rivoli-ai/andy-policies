// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.GrpcServices;

/// <summary>
/// P7.6 (#64) — runtime tests for
/// <c>Andy.Policies.Api.GrpcServices.Authorization.RbacServerInterceptor</c>.
/// The interceptor lives at the gRPC pipeline boundary, so a unit-shaped
/// test against a hand-rolled <see cref="ServerCallContext"/> would not
/// cover what we actually care about: that <c>AddGrpc(...).Interceptors.Add</c>
/// in <c>Program.cs</c> wires it for every enforced RPC, that DI resolves
/// the per-call <see cref="IRbacChecker"/>, and that denials surface as
/// <c>RpcException</c>s the client SDK can branch on.
/// </summary>
/// <remarks>
/// <para>
/// Companion files:
/// </para>
/// <list type="bullet">
///   <item><see cref="Andy.Policies.Tests.Integration.Authorization.GrpcPermissionMapCoverageTests"/>
///         — proves every enforced rpc has a permission code.</item>
///   <item><c>OverrideToolsRbacTests</c> — proves the MCP <c>McpRbacGuard</c>
///         contract; this file is the gRPC-side equivalent.</item>
/// </list>
/// <para>
/// <b>Why a programmable RBAC instead of the WireMock fixture from P7.5?</b>
/// The wire stub is appropriate for asserting the HTTP body shape of the
/// outgoing andy-rbac call. Here we want fast, in-process control over
/// the decision and observability of the captured permission code; a
/// recording stub is the right tool for that.
/// </para>
/// </remarks>
public class RbacInterceptorTests
{
    private sealed record CapturedRbacCall(string SubjectId, string PermissionCode, string? ResourceInstanceId);

    private sealed class ProgrammableRbac : IRbacChecker
    {
        public RbacDecision NextDecision { get; set; } = new(true, "test-allow");

        public List<CapturedRbacCall> Calls { get; } = new();

        public Task<RbacDecision> CheckAsync(
            string subjectId, string permissionCode, IReadOnlyList<string> groups,
            string? resourceInstanceId, CancellationToken ct)
        {
            Calls.Add(new CapturedRbacCall(subjectId, permissionCode, resourceInstanceId));
            return Task.FromResult(NextDecision);
        }
    }

    /// <summary>
    /// Variant of <see cref="PoliciesApiFactory"/> that lets a single test
    /// program the <see cref="IRbacChecker"/>. Cannot reuse the stock
    /// factory because it pins an allow-all stub at construction time.
    /// </summary>
    private sealed class RbacInterceptorFactory : WebApplicationFactory<Program>
    {
        public ProgrammableRbac Rbac { get; } = new();

        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public RbacInterceptorFactory()
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
                    ["AndyRbac:BaseUrl"] = "https://test-rbac.invalid",
                });
            });
            builder.ConfigureServices(services =>
            {
                var ctxDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (ctxDescriptor is not null) services.Remove(ctxDescriptor);
                services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

                var rbacDescriptors = services
                    .Where(d => d.ServiceType == typeof(IRbacChecker))
                    .ToList();
                foreach (var d in rbacDescriptors) services.Remove(d);
                services.AddSingleton<IRbacChecker>(Rbac);

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
    }

    private static GrpcChannel ChannelFor(WebApplicationFactory<Program> factory)
    {
        var handler = factory.Server.CreateHandler();
        return GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
    }

    [Fact]
    public async Task EnforcedRpc_RbacAllow_Succeeds_AndCallsCheckerWithMappedPermission()
    {
        using var factory = new RbacInterceptorFactory();
        // Force creation of the in-process server before pulling the handler.
        using var _ = factory.CreateClient();
        using var channel = ChannelFor(factory);
        var client = new PolicyService.PolicyServiceClient(channel);

        // The call must complete without an RpcException — that alone
        // proves the interceptor passed. We don't assert a specific
        // response shape because Program.cs seeds the DB on EnsureCreated.
        var resp = await client.ListPoliciesAsync(new ListPoliciesRequest());
        resp.Should().NotBeNull();

        // The interceptor delegates to GrpcMethodPermissionMap, so this
        // doubles as an end-to-end assertion that ListPolicies maps to
        // andy-policies:policy:read (#64 mapping).
        factory.Rbac.Calls.Should().ContainSingle(
            c => c.PermissionCode == "andy-policies:policy:read");
    }

    [Fact]
    public async Task EnforcedRpc_RbacDeny_Throws_PermissionDenied_WithReason()
    {
        using var factory = new RbacInterceptorFactory();
        factory.Rbac.NextDecision = new RbacDecision(false, "no-permission");
        using var _ = factory.CreateClient();
        using var channel = ChannelFor(factory);
        var client = new PolicyService.PolicyServiceClient(channel);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => client.ListPoliciesAsync(new ListPoliciesRequest()).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Status.Detail.Should().Be(
            "no-permission",
            "the andy-rbac decision Reason flows through unchanged so admin " +
            "triage can correlate denial with the originating role / rule");
    }

    [Fact]
    public async Task EnforcedRpc_RbacDeny_OnMutatingCall_DoesNotPersist()
    {
        using var factory = new RbacInterceptorFactory();
        factory.Rbac.NextDecision = new RbacDecision(false, "missing-author-role");
        using var http = factory.CreateClient();
        using var channel = ChannelFor(factory);
        var client = new PolicyService.PolicyServiceClient(channel);

        var slug = $"deny-{Guid.NewGuid():N}".Substring(0, 14);
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => client.CreateDraftAsync(new CreateDraftRequest
            {
                Name = slug,
                Description = "",
                Summary = "summary",
                Enforcement = "Must",
                Severity = "Critical",
                RulesJson = "{}",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.PermissionDenied);

        // No row should have been written — proves the deny ran *before*
        // the gRPC service handler, not after.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Policies.AsNoTracking().Where(p => p.Name == slug).ToListAsync();
        rows.Should().BeEmpty(
            "an RBAC denial that still produced a Policy row would mean the " +
            "interceptor ran *after* the service body — the entire fail-closed " +
            "guarantee depends on running before");
    }

    // The Items bypass is pinned at the unit level by
    // GrpcPermissionMapCoverageTests.ItemsServiceIsBypassedNotMapped;
    // a runtime version would require generating a client stub for items.proto
    // (it ships GrpcServices="Server" — no client class), and the
    // interceptor's bypass branch is a single IsEnforcedService check.

    [Fact]
    public async Task EnforcedRpc_AfterAllow_ResponsePassesThroughUnmodified()
    {
        // Defends against an interceptor regression that wraps responses
        // (e.g. swallowing fields, replacing error envelopes) — the
        // happy-path response from the service must reach the caller as-is.
        using var factory = new RbacInterceptorFactory();
        using var http = factory.CreateClient();
        using var channel = ChannelFor(factory);
        var client = new PolicyService.PolicyServiceClient(channel);

        var slug = $"pass-{Guid.NewGuid():N}".Substring(0, 14);
        var created = await client.CreateDraftAsync(new CreateDraftRequest
        {
            Name = slug,
            Description = "",
            Summary = "summary",
            Enforcement = "Must",
            Severity = "Critical",
            RulesJson = "{}",
        });

        created.Version.Version.Should().Be(1);
        created.Version.State.Should().Be("Draft");
        // CreateDraft requires both author + read in sequence (PolicyService
        // calls back into ListPolicies-style read paths internally for some
        // queries). At minimum, the author permission must show up.
        factory.Rbac.Calls.Should().Contain(
            c => c.PermissionCode == "andy-policies:policy:author");
    }
}
