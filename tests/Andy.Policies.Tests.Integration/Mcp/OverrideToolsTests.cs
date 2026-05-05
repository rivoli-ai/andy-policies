// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Api.Mcp;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.Settings;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Mcp;

/// <summary>
/// Tests for <see cref="OverrideTools"/> (P5.6, story
/// rivoli-ai/andy-policies#59). Drives the static tool methods
/// directly against a real <see cref="OverrideService"/> backed by
/// EF Core InMemory plus stubs for <see cref="IExperimentalOverridesGate"/>,
/// <see cref="IDomainEventDispatcher"/>, and <see cref="IRbacChecker"/>.
/// Verifies the wire contract returned to MCP agents: structured
/// JSON DTOs on success, prefixed error codes
/// (<c>policy.override.{disabled,invalid_argument,self_approval_forbidden,
/// not_found,invalid_state,rbac_denied}</c>) on failure, and the
/// actor-fallback firewall.
/// </summary>
public class OverrideToolsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class StubGate : IExperimentalOverridesGate
    {
        public bool IsEnabled { get; set; } = true;
    }

    private sealed class AllowRbac : IRbacChecker
    {
        public Task<RbacDecision> CheckAsync(
            string subjectId, string permissionCode, IReadOnlyList<string> groups,
            string? resourceInstanceId, CancellationToken ct)
            => Task.FromResult(new RbacDecision(true, "test-allow"));
    }

    private sealed class NoopDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
            where TEvent : notnull => Task.CompletedTask;
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static (OverrideService service, AppDbContext db, StubGate gate) NewServices()
    {
        var db = NewDb();
        var service = new OverrideService(db, new AllowRbac(), new NoopDispatcher(), TimeProvider.System);
        return (service, db, new StubGate { IsEnabled = true });
    }

    private static IHttpContextAccessor AccessorFor(string? subjectId)
    {
        var ctx = new DefaultHttpContext();
        if (subjectId is not null)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, subjectId),
            }, authenticationType: "Test"));
        }
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static async Task<PolicyVersion> SeedActiveAsync(AppDbContext db, string name = "p1")
    {
        var policy = new Policy { Id = Guid.NewGuid(), Name = name, CreatedBySubjectId = "u1" };
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
            RulesJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "u1",
            ProposerSubjectId = "u1",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return version;
    }

    private static async Task<OverrideDto> SeedProposedAsync(
        OverrideService svc, AppDbContext db, string proposer = "user:proposer", string scopeRef = "user:42")
    {
        var version = await SeedActiveAsync(db, $"p-{Guid.NewGuid():n}");
        return await svc.ProposeAsync(
            new ProposeOverrideRequest(
                version.Id,
                OverrideScopeKind.Principal,
                scopeRef,
                OverrideEffect.Exempt,
                ReplacementPolicyVersionId: null,
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(24),
                Rationale: "fixture"),
            proposer);
    }

    // ----- Propose ------------------------------------------------------

    [Fact]
    public async Task Propose_GateOff_ReturnsDisabledErrorCode()
    {
        var (svc, db, gate) = NewServices();
        var version = await SeedActiveAsync(db);
        gate.IsEnabled = false;

        var output = await OverrideTools.Propose(
            svc, gate, AccessorFor("user:proposer"),
            version.Id.ToString(), "Principal", "user:42",
            "Exempt", DateTimeOffset.UtcNow.AddDays(1).ToString("o"), "rationale");

        output.Should().StartWith("policy.override.disabled:");
    }

    [Fact]
    public async Task Propose_NoSubject_ReturnsAuthenticationRequired()
    {
        var (svc, db, gate) = NewServices();
        var version = await SeedActiveAsync(db);

        var output = await OverrideTools.Propose(
            svc, gate, AccessorFor(null),
            version.Id.ToString(), "Principal", "user:42",
            "Exempt", DateTimeOffset.UtcNow.AddDays(1).ToString("o"), "rationale");

        output.Should().StartWith("Authentication required");
    }

    [Fact]
    public async Task Propose_HappyPath_ReturnsJsonDto()
    {
        var (svc, db, gate) = NewServices();
        var version = await SeedActiveAsync(db);

        var output = await OverrideTools.Propose(
            svc, gate, AccessorFor("user:proposer"),
            version.Id.ToString(), "Principal", "user:42",
            "Exempt", DateTimeOffset.UtcNow.AddDays(1).ToString("o"),
            "expedite vendor-blocked story");

        var dto = JsonSerializer.Deserialize<OverrideDto>(output, JsonOptions);
        dto.Should().NotBeNull();
        dto!.State.Should().Be(OverrideState.Proposed);
        dto.PolicyVersionId.Should().Be(version.Id);
        dto.ProposerSubjectId.Should().Be("user:proposer");
    }

    [Fact]
    public async Task Propose_InvalidGuid_ReturnsInvalidArgument()
    {
        var (svc, _, gate) = NewServices();

        var output = await OverrideTools.Propose(
            svc, gate, AccessorFor("user:proposer"),
            "not-a-guid", "Principal", "user:42",
            "Exempt", DateTimeOffset.UtcNow.AddDays(1).ToString("o"), "rationale");

        output.Should().StartWith("policy.override.invalid_argument:");
    }

    [Fact]
    public async Task Propose_BadEnumValue_ReturnsInvalidArgument()
    {
        var (svc, db, gate) = NewServices();
        var version = await SeedActiveAsync(db);

        var output = await OverrideTools.Propose(
            svc, gate, AccessorFor("user:proposer"),
            version.Id.ToString(), "NotAScopeKind", "user:42",
            "Exempt", DateTimeOffset.UtcNow.AddDays(1).ToString("o"), "rationale");

        output.Should().StartWith("policy.override.invalid_argument:");
        output.Should().Contain("scopeKind");
    }

    // ----- Approve ------------------------------------------------------

    [Fact]
    public async Task Approve_HappyPath_ReturnsJsonWithApprovedState()
    {
        var (svc, db, gate) = NewServices();
        var proposed = await SeedProposedAsync(svc, db, proposer: "user:proposer");

        var output = await OverrideTools.Approve(
            svc, gate, AccessorFor("user:approver"),
            proposed.Id.ToString());

        var dto = JsonSerializer.Deserialize<OverrideDto>(output, JsonOptions);
        dto!.State.Should().Be(OverrideState.Approved);
        dto.ApproverSubjectId.Should().Be("user:approver");
    }

    [Fact]
    public async Task Approve_ByProposer_ReturnsSelfApprovalForbidden()
    {
        var (svc, db, gate) = NewServices();
        var proposed = await SeedProposedAsync(svc, db, proposer: "user:proposer");

        var output = await OverrideTools.Approve(
            svc, gate, AccessorFor("user:proposer"),
            proposed.Id.ToString());

        output.Should().StartWith("policy.override.self_approval_forbidden:");
    }

    [Fact]
    public async Task Approve_AlreadyApproved_ReturnsInvalidState()
    {
        var (svc, db, gate) = NewServices();
        var proposed = await SeedProposedAsync(svc, db, proposer: "user:proposer");
        await svc.ApproveAsync(proposed.Id, "user:approver");

        var output = await OverrideTools.Approve(
            svc, gate, AccessorFor("user:other"),
            proposed.Id.ToString());

        output.Should().StartWith("policy.override.invalid_state:");
    }

    [Fact]
    public async Task Approve_UnknownId_ReturnsNotFound()
    {
        var (svc, _, gate) = NewServices();

        var output = await OverrideTools.Approve(
            svc, gate, AccessorFor("user:approver"),
            Guid.NewGuid().ToString());

        output.Should().StartWith("policy.override.not_found:");
    }

    [Fact]
    public async Task Approve_GateOff_ReturnsDisabledErrorCode()
    {
        var (svc, db, gate) = NewServices();
        var proposed = await SeedProposedAsync(svc, db);
        gate.IsEnabled = false;

        var output = await OverrideTools.Approve(
            svc, gate, AccessorFor("user:approver"),
            proposed.Id.ToString());

        output.Should().StartWith("policy.override.disabled:");
    }

    // ----- Revoke -------------------------------------------------------

    [Fact]
    public async Task Revoke_HappyPath_FromProposed_ReturnsJsonWithRevokedState()
    {
        var (svc, db, gate) = NewServices();
        var proposed = await SeedProposedAsync(svc, db);

        var output = await OverrideTools.Revoke(
            svc, gate, AccessorFor("user:approver"),
            proposed.Id.ToString(), "withdrawn");

        var dto = JsonSerializer.Deserialize<OverrideDto>(output, JsonOptions);
        dto!.State.Should().Be(OverrideState.Revoked);
        dto.RevocationReason.Should().Be("withdrawn");
    }

    [Fact]
    public async Task Revoke_BlankReason_ReturnsInvalidArgument()
    {
        var (svc, db, gate) = NewServices();
        var proposed = await SeedProposedAsync(svc, db);

        var output = await OverrideTools.Revoke(
            svc, gate, AccessorFor("user:approver"),
            proposed.Id.ToString(), "   ");

        output.Should().StartWith("policy.override.invalid_argument:");
    }

    // ----- Read tools bypass gate --------------------------------------

    [Fact]
    public async Task List_GateOff_StillReturnsRows()
    {
        var (svc, db, gate) = NewServices();
        await SeedProposedAsync(svc, db);
        gate.IsEnabled = false;

        var output = await OverrideTools.List(svc);

        // Read tools don't take the gate; output is JSON array.
        output.TrimStart().Should().StartWith("[");
        var rows = JsonSerializer.Deserialize<List<OverrideDto>>(output, JsonOptions);
        rows.Should().NotBeEmpty();
    }

    [Fact]
    public async Task List_FiltersByState()
    {
        var (svc, db, _) = NewServices();
        var a = await SeedProposedAsync(svc, db, scopeRef: "user:1");
        await SeedProposedAsync(svc, db, scopeRef: "user:2");
        await svc.ApproveAsync(a.Id, "user:approver");

        var output = await OverrideTools.List(svc, state: "Approved");
        var rows = JsonSerializer.Deserialize<List<OverrideDto>>(output, JsonOptions);

        rows.Should().ContainSingle().Which.Id.Should().Be(a.Id);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNotFound()
    {
        var (svc, _, _) = NewServices();

        var output = await OverrideTools.Get(svc, Guid.NewGuid().ToString());

        output.Should().StartWith("policy.override.not_found:");
    }

    [Fact]
    public async Task Active_BypassesGate_AndFiltersToApproved()
    {
        var (svc, db, gate) = NewServices();
        var live = await SeedProposedAsync(svc, db, scopeRef: "user:42");
        await svc.ApproveAsync(live.Id, "user:approver");
        await SeedProposedAsync(svc, db, scopeRef: "user:42"); // proposed only — should NOT appear

        gate.IsEnabled = false;
        var output = await OverrideTools.Active(svc, "Principal", "user:42");
        gate.IsEnabled = true;

        var rows = JsonSerializer.Deserialize<List<OverrideDto>>(output, JsonOptions);
        rows.Should().ContainSingle().Which.Id.Should().Be(live.Id);
    }

    [Fact]
    public async Task Active_BadScopeKind_ReturnsInvalidArgument()
    {
        var (svc, _, _) = NewServices();

        var output = await OverrideTools.Active(svc, "NotAScope", "user:42");

        output.Should().StartWith("policy.override.invalid_argument:");
    }
}
