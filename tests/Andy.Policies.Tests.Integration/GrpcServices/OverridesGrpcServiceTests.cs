// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Settings;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.GrpcServices;

/// <summary>
/// End-to-end gRPC tests for the override surface (P5.7, story
/// rivoli-ai/andy-policies#60). Exercises every RPC over a real
/// HTTP/2 channel against the test server, verifying the proto
/// contract, generated stubs, exception → status-code mapping
/// (PERMISSION_DENIED with trailers <c>override_disabled=1</c> /
/// <c>reason=self_approval</c>; FAILED_PRECONDITION for invalid
/// state; NOT_FOUND for unknown ids), and the actor-fallback
/// firewall.
/// </summary>
public class OverridesGrpcServiceTests : IDisposable
{
    private sealed class StubGate : IExperimentalOverridesGate
    {
        public bool IsEnabled { get; set; } = true;
    }

    private sealed class OverridesGrpcFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public StubGate Gate { get; } = new();

        public OverridesGrpcFactory()
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

    private readonly OverridesGrpcFactory _factory = new();
    private readonly GrpcChannel _channel;
    private readonly Andy.Policies.Api.Protos.OverridesService.OverridesServiceClient _overrides;
    private readonly HttpClient _http;

    public OverridesGrpcServiceTests()
    {
        // Force creation of the test server before grabbing the handler.
        _http = _factory.CreateClient();

        var handler = _factory.Server.CreateHandler();
        _channel = GrpcChannel.ForAddress(_factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
        _overrides = new Andy.Policies.Api.Protos.OverridesService.OverridesServiceClient(_channel);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _http.Dispose();
        _factory.Dispose();
    }

    private async Task<Guid> CreateActivePolicyVersionAsync(string slug)
    {
        // Easiest path to an Active version: use the existing REST flow
        // (CreatePolicy → Publish). Same fixture pattern as the REST
        // override controller tests.
        var resp = await _http.PostAsJsonAsync("/api/policies", new
        {
            Name = slug,
            Description = (string?)null,
            Summary = "summary",
            Enforcement = "Must",
            Severity = "Critical",
            Scopes = Array.Empty<string>(),
            RulesJson = "{}",
        });
        resp.EnsureSuccessStatusCode();
        var draft = await resp.Content.ReadFromJsonAsync<DraftPayload>();
        var publishResp = await _http.PostAsJsonAsync(
            $"/api/policies/{draft!.PolicyId}/versions/{draft.Id}/publish",
            new { Rationale = "go-live" });
        publishResp.EnsureSuccessStatusCode();
        var active = await publishResp.Content.ReadFromJsonAsync<DraftPayload>();
        return active!.Id;
    }

    private sealed record DraftPayload(Guid Id, Guid PolicyId);

    private static ProposeOverrideRequest ExemptRequest(
        Guid policyVersionId,
        string scopeRef = "user:42",
        DateTimeOffset? expiresAt = null,
        string rationale = "expedite vendor-blocked story") => new()
        {
            PolicyVersionId = policyVersionId.ToString(),
            ScopeKind = ProtoScopeKind.ScopeKindPrincipal,
            ScopeRef = scopeRef,
            Effect = ProtoEffectKind.EffectKindExempt,
            ExpiresAt = (expiresAt ?? DateTimeOffset.UtcNow.AddHours(24)).ToString("o"),
            Rationale = rationale,
        };

    private static Metadata HeadersFor(string subject)
        => new() { { TestAuthHandler.SubjectHeader, subject } };

    private static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}".Substring(0, 16);

    [Fact]
    public async Task ProposeOverride_HappyPath_ReturnsProposedState()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-prop"));

        var resp = await _overrides.ProposeOverrideAsync(ExemptRequest(pvid));

        resp.State.Should().Be(ProtoOverrideState.OverrideStateProposed);
        resp.PolicyVersionId.Should().Be(pvid.ToString());
        resp.ScopeKind.Should().Be(ProtoScopeKind.ScopeKindPrincipal);
        resp.Effect.Should().Be(ProtoEffectKind.EffectKindExempt);
    }

    [Fact]
    public async Task ProposeOverride_GateOff_ThrowsPermissionDeniedWithTrailer()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-gate"));
        _factory.Gate.IsEnabled = false;
        try
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                _overrides.ProposeOverrideAsync(ExemptRequest(pvid)).ResponseAsync);

            ex.StatusCode.Should().Be(StatusCode.PermissionDenied);
            ex.Trailers.Should().Contain(t =>
                t.Key == "override_disabled" && t.Value == "1");
        }
        finally
        {
            _factory.Gate.IsEnabled = true;
        }
    }

    [Fact]
    public async Task ProposeOverride_BlankRationale_ThrowsInvalidArgument()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-rat"));
        var bad = ExemptRequest(pvid, rationale: "   ");

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _overrides.ProposeOverrideAsync(bad).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ProposeOverride_BadGuid_ThrowsInvalidArgument()
    {
        var bad = new ProposeOverrideRequest
        {
            PolicyVersionId = "not-a-guid",
            ScopeKind = ProtoScopeKind.ScopeKindPrincipal,
            ScopeRef = "user:42",
            Effect = ProtoEffectKind.EffectKindExempt,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToString("o"),
            Rationale = "rationale",
        };

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _overrides.ProposeOverrideAsync(bad).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ApproveOverride_HappyPath_TransitionsToApproved()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-app"));
        var proposed = await _overrides.ProposeOverrideAsync(
            ExemptRequest(pvid), HeadersFor("user:proposer"));

        var approved = await _overrides.ApproveOverrideAsync(
            new ApproveOverrideRequest { Id = proposed.Id },
            HeadersFor("user:approver"));

        approved.State.Should().Be(ProtoOverrideState.OverrideStateApproved);
        approved.ApproverSubjectId.Should().Be("user:approver");
    }

    [Fact]
    public async Task ApproveOverride_ByProposer_ThrowsPermissionDeniedWithSelfApprovalTrailer()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-self"));
        var proposed = await _overrides.ProposeOverrideAsync(
            ExemptRequest(pvid), HeadersFor("user:proposer"));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _overrides.ApproveOverrideAsync(
                new ApproveOverrideRequest { Id = proposed.Id },
                HeadersFor("user:proposer")).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Trailers.Should().Contain(t => t.Key == "reason" && t.Value == "self_approval");
    }

    [Fact]
    public async Task ApproveOverride_AlreadyApproved_ThrowsFailedPrecondition()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-twice"));
        var proposed = await _overrides.ProposeOverrideAsync(
            ExemptRequest(pvid), HeadersFor("user:proposer"));
        await _overrides.ApproveOverrideAsync(
            new ApproveOverrideRequest { Id = proposed.Id },
            HeadersFor("user:approver"));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _overrides.ApproveOverrideAsync(
                new ApproveOverrideRequest { Id = proposed.Id },
                HeadersFor("user:third")).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task ApproveOverride_UnknownId_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _overrides.ApproveOverrideAsync(
                new ApproveOverrideRequest { Id = Guid.NewGuid().ToString() }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task RevokeOverride_HappyPath_FromProposed_TransitionsToRevoked()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-rev"));
        var proposed = await _overrides.ProposeOverrideAsync(ExemptRequest(pvid));

        var revoked = await _overrides.RevokeOverrideAsync(new RevokeOverrideRequest
        {
            Id = proposed.Id,
            RevocationReason = "withdrawn",
        });

        revoked.State.Should().Be(ProtoOverrideState.OverrideStateRevoked);
        revoked.RevocationReason.Should().Be("withdrawn");
    }

    [Fact]
    public async Task RevokeOverride_BlankReason_ThrowsInvalidArgument()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-rev0"));
        var proposed = await _overrides.ProposeOverrideAsync(ExemptRequest(pvid));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _overrides.RevokeOverrideAsync(new RevokeOverrideRequest
            {
                Id = proposed.Id,
                RevocationReason = "   ",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task GetOverride_UnknownId_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _overrides.GetOverrideAsync(
                new GetOverrideRequest { Id = Guid.NewGuid().ToString() }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task GetOverride_Existing_ReturnsMessage()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-get"));
        var proposed = await _overrides.ProposeOverrideAsync(ExemptRequest(pvid));

        var resp = await _overrides.GetOverrideAsync(
            new GetOverrideRequest { Id = proposed.Id });

        resp.Id.Should().Be(proposed.Id);
    }

    [Fact]
    public async Task ListOverrides_FiltersByState()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-list"));
        var a = await _overrides.ProposeOverrideAsync(
            ExemptRequest(pvid, "user:a"), HeadersFor("user:proposer"));
        await _overrides.ProposeOverrideAsync(
            ExemptRequest(pvid, "user:b"), HeadersFor("user:proposer"));
        await _overrides.ApproveOverrideAsync(
            new ApproveOverrideRequest { Id = a.Id },
            HeadersFor("user:approver"));

        var resp = await _overrides.ListOverridesAsync(new ListOverridesRequest
        {
            State = ProtoOverrideState.OverrideStateApproved,
        });

        resp.Items.Should().ContainSingle().Which.Id.Should().Be(a.Id);
    }

    [Fact]
    public async Task GetActiveOverrides_BypassesGate_AndFiltersToApproved()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-act"));
        var proposed = await _overrides.ProposeOverrideAsync(
            ExemptRequest(pvid, "user:42"), HeadersFor("user:proposer"));
        await _overrides.ApproveOverrideAsync(
            new ApproveOverrideRequest { Id = proposed.Id },
            HeadersFor("user:approver"));

        _factory.Gate.IsEnabled = false;
        try
        {
            var resp = await _overrides.GetActiveOverridesAsync(new GetActiveOverridesRequest
            {
                ScopeKind = ProtoScopeKind.ScopeKindPrincipal,
                ScopeRef = "user:42",
            });

            resp.Items.Should().ContainSingle().Which.Id.Should().Be(proposed.Id);
        }
        finally
        {
            _factory.Gate.IsEnabled = true;
        }
    }

    [Fact]
    public async Task GetActiveOverrides_MissingScopeRef_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _overrides.GetActiveOverridesAsync(new GetActiveOverridesRequest
            {
                ScopeKind = ProtoScopeKind.ScopeKindPrincipal,
                ScopeRef = string.Empty,
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ProposeOverride_UnspecifiedScopeKind_ThrowsInvalidArgument()
    {
        var pvid = await CreateActivePolicyVersionAsync(Slug("ovr-grpc-uns"));
        var bad = ExemptRequest(pvid);
        bad.ScopeKind = ProtoScopeKind.ScopeKindUnspecified;

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _overrides.ProposeOverrideAsync(bad).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
