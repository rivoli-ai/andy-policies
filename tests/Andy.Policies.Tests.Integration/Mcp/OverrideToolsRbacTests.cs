// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
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
using Xunit;

namespace Andy.Policies.Tests.Integration.Mcp;

/// <summary>
/// P7.6 (#64) — RBAC enforcement on the override MCP tools. Pins the
/// contract that <see cref="OverrideTools.Propose"/>,
/// <see cref="OverrideTools.Approve"/>, and <see cref="OverrideTools.Revoke"/>
/// each invoke <see cref="Andy.Policies.Api.Mcp.Authorization.McpRbacGuard"/>
/// with the permission code from the P7.1 manifest, the resource
/// instance derived from the tool arguments, and the JWT
/// <c>groups</c> claim — and translate denials into the typed
/// <c>policy.override.forbidden</c> tool error rather than letting the
/// guard exception propagate.
/// </summary>
/// <remarks>
/// <para>
/// Companion to <see cref="OverrideToolsTests"/>: that class fixes the
/// gate / state-machine / serialization wire contract; this class fixes
/// the authorization wire contract. They share the same
/// <see cref="OverrideService"/> + EF Core InMemory backing so a tool
/// that passes both files behaves the same against a live andy-rbac.
/// </para>
/// <para>
/// Resource-instance shapes (kept aligned with REST P7.4 / gRPC P7.6):
/// </para>
/// <list type="bullet">
///   <item><c>propose</c> → <c>scopeRef</c> as-is (e.g. <c>"user:42"</c>);
///         the propose call has no override id yet.</item>
///   <item><c>approve</c>, <c>revoke</c> → <c>"override:{guid}"</c> so
///         per-instance grants can be authored once the row exists.</item>
/// </list>
/// </remarks>
public class OverrideToolsRbacTests
{
    private sealed class StubGate : IExperimentalOverridesGate
    {
        public bool IsEnabled { get; set; } = true;
    }

    private sealed class NoopDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
            where TEvent : notnull => Task.CompletedTask;
    }

    private sealed record CapturedCall(
        string SubjectId,
        string Permission,
        IReadOnlyList<string> Groups,
        string? ResourceInstanceId);

    private sealed class RecordingRbac : IRbacChecker
    {
        public RbacDecision NextDecision { get; set; } = new(true, "test-allow");

        public List<CapturedCall> Calls { get; } = new();

        public Task<RbacDecision> CheckAsync(
            string subjectId, string permissionCode, IReadOnlyList<string> groups,
            string? resourceInstanceId, CancellationToken ct)
        {
            Calls.Add(new CapturedCall(subjectId, permissionCode, groups.ToList(), resourceInstanceId));
            return Task.FromResult(NextDecision);
        }
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
        // The OverrideService takes its own IRbacChecker for the
        // propose/approve domain rules (not the McpRbacGuard surface
        // this test class targets). Hand it an unconditional allow stub
        // so we isolate the *MCP* guard signal we're measuring.
        var serviceLayerAllow = new RecordingRbac();
        var service = new OverrideService(db, serviceLayerAllow, new NoopDispatcher(), TimeProvider.System);
        return (service, db, new StubGate { IsEnabled = true });
    }

    private static IHttpContextAccessor AccessorFor(string? subjectId, params string[] groups)
    {
        var ctx = new DefaultHttpContext();
        if (subjectId is not null)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subjectId) };
            claims.AddRange(groups.Select(g => new Claim("groups", g)));
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
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
    public async Task Propose_RbacAllow_CallsCheckerWithProposePermissionAndScopeRefResource()
    {
        var (svc, db, gate) = NewServices();
        var version = await SeedActiveAsync(db);
        var rbac = new RecordingRbac { NextDecision = new(true, "role:override-author") };

        var output = await OverrideTools.Propose(
            svc, gate, AccessorFor("user:proposer"), rbac,
            version.Id.ToString(), "Principal", "user:42",
            "Exempt", DateTimeOffset.UtcNow.AddDays(1).ToString("o"), "rationale");

        // Tool returned the JSON DTO, not an error string.
        output.TrimStart().Should().StartWith("{");
        rbac.Calls.Should().ContainSingle();
        rbac.Calls[0].SubjectId.Should().Be("user:proposer");
        rbac.Calls[0].Permission.Should().Be("andy-policies:override:propose");
        rbac.Calls[0].ResourceInstanceId.Should().Be(
            "user:42",
            "propose has no override id yet, so the scope ref is the only " +
            "instance handle a per-grant rule could attach to");
    }

    [Fact]
    public async Task Propose_RbacDeny_ReturnsForbiddenWithReason_AndDoesNotPersist()
    {
        var (svc, db, gate) = NewServices();
        var version = await SeedActiveAsync(db);
        var rbac = new RecordingRbac { NextDecision = new(false, "no-permission") };

        var output = await OverrideTools.Propose(
            svc, gate, AccessorFor("user:nope"), rbac,
            version.Id.ToString(), "Principal", "user:42",
            "Exempt", DateTimeOffset.UtcNow.AddDays(1).ToString("o"), "rationale");

        output.Should().StartWith("policy.override.forbidden:");
        output.Should().Contain("no-permission");

        // The denial must short-circuit before the service runs.
        var rows = await db.Overrides.AsNoTracking().ToListAsync();
        rows.Should().BeEmpty(
            "an RBAC denial that still wrote a row would defeat the entire " +
            "guard — the audit trail would carry a proposed override the " +
            "caller was not allowed to author");
    }

    [Fact]
    public async Task Propose_GateOff_ShortCircuits_NoRbacCall()
    {
        var (svc, db, gate) = NewServices();
        var version = await SeedActiveAsync(db);
        gate.IsEnabled = false;
        var rbac = new RecordingRbac();

        var output = await OverrideTools.Propose(
            svc, gate, AccessorFor("user:proposer"), rbac,
            version.Id.ToString(), "Principal", "user:42",
            "Exempt", DateTimeOffset.UtcNow.AddDays(1).ToString("o"), "rationale");

        output.Should().StartWith("policy.override.disabled:");
        rbac.Calls.Should().BeEmpty(
            "the experimental-overrides gate is the outermost guard; " +
            "checking RBAC for a disabled feature would leak permission " +
            "lookups against andy-rbac for traffic that's already null-routed");
    }

    [Fact]
    public async Task Propose_NoSubjectClaim_ShortCircuits_NoRbacCall()
    {
        var (svc, db, gate) = NewServices();
        var version = await SeedActiveAsync(db);
        var rbac = new RecordingRbac();

        var output = await OverrideTools.Propose(
            svc, gate, AccessorFor(null), rbac,
            version.Id.ToString(), "Principal", "user:42",
            "Exempt", DateTimeOffset.UtcNow.AddDays(1).ToString("o"), "rationale");

        output.Should().StartWith("Authentication required");
        rbac.Calls.Should().BeEmpty(
            "asking andy-rbac whether 'no subject' has a permission is " +
            "incoherent — the unauthenticated branch must reject before the check");
    }

    [Fact]
    public async Task Propose_GroupsClaim_FlowsThroughToRbac()
    {
        var (svc, db, gate) = NewServices();
        var version = await SeedActiveAsync(db);
        var rbac = new RecordingRbac();

        await OverrideTools.Propose(
            svc, gate,
            AccessorFor("user:proposer", "team:authors", "team:eu"),
            rbac,
            version.Id.ToString(), "Principal", "user:42",
            "Exempt", DateTimeOffset.UtcNow.AddDays(1).ToString("o"), "rationale");

        rbac.Calls.Should().ContainSingle();
        rbac.Calls[0].Groups.Should().BeEquivalentTo(new[] { "team:authors", "team:eu" });
    }

    // ----- Approve ------------------------------------------------------

    [Fact]
    public async Task Approve_RbacAllow_CallsCheckerWithOverrideIdResource()
    {
        var (svc, db, gate) = NewServices();
        var proposed = await SeedProposedAsync(svc, db, proposer: "user:proposer");
        var rbac = new RecordingRbac();

        await OverrideTools.Approve(
            svc, gate, AccessorFor("user:approver"), rbac,
            proposed.Id.ToString());

        rbac.Calls.Should().ContainSingle();
        rbac.Calls[0].SubjectId.Should().Be("user:approver");
        rbac.Calls[0].Permission.Should().Be("andy-policies:override:approve");
        rbac.Calls[0].ResourceInstanceId.Should().Be(
            $"override:{proposed.Id}",
            "per-instance approve grants need to attach to the override id; " +
            "scope-ref keying would let an approver-on-cohort-X also approve " +
            "rows scoped to cohort-Y");
    }

    [Fact]
    public async Task Approve_RbacDeny_ReturnsForbidden_AndDoesNotChangeState()
    {
        var (svc, db, gate) = NewServices();
        var proposed = await SeedProposedAsync(svc, db, proposer: "user:proposer");
        var rbac = new RecordingRbac { NextDecision = new(false, "missing-role") };

        var output = await OverrideTools.Approve(
            svc, gate, AccessorFor("user:approver"), rbac,
            proposed.Id.ToString());

        output.Should().StartWith("policy.override.forbidden:");
        output.Should().Contain("missing-role");

        var row = await db.Overrides.AsNoTracking().SingleAsync(o => o.Id == proposed.Id);
        row.State.Should().Be(
            OverrideState.Proposed,
            "deny must run before ApproveAsync — otherwise the row would " +
            "transition to Approved and the deny would surface as a no-op");
    }

    [Fact]
    public async Task Approve_GateOff_ShortCircuits_NoRbacCall()
    {
        var (svc, db, gate) = NewServices();
        var proposed = await SeedProposedAsync(svc, db);
        gate.IsEnabled = false;
        var rbac = new RecordingRbac();

        await OverrideTools.Approve(
            svc, gate, AccessorFor("user:approver"), rbac,
            proposed.Id.ToString());

        rbac.Calls.Should().BeEmpty();
    }

    // ----- Revoke -------------------------------------------------------

    [Fact]
    public async Task Revoke_RbacAllow_CallsCheckerWithOverrideIdResource()
    {
        var (svc, db, gate) = NewServices();
        var proposed = await SeedProposedAsync(svc, db);
        var rbac = new RecordingRbac();

        await OverrideTools.Revoke(
            svc, gate, AccessorFor("user:approver"), rbac,
            proposed.Id.ToString(), "withdrawn");

        rbac.Calls.Should().ContainSingle();
        rbac.Calls[0].Permission.Should().Be("andy-policies:override:revoke");
        rbac.Calls[0].ResourceInstanceId.Should().Be($"override:{proposed.Id}");
    }

    [Fact]
    public async Task Revoke_RbacDeny_ReturnsForbidden_AndDoesNotChangeState()
    {
        var (svc, db, gate) = NewServices();
        var proposed = await SeedProposedAsync(svc, db);
        var rbac = new RecordingRbac { NextDecision = new(false, "no-revoke-role") };

        var output = await OverrideTools.Revoke(
            svc, gate, AccessorFor("user:approver"), rbac,
            proposed.Id.ToString(), "withdrawn");

        output.Should().StartWith("policy.override.forbidden:");
        output.Should().Contain("no-revoke-role");
        var row = await db.Overrides.AsNoTracking().SingleAsync(o => o.Id == proposed.Id);
        row.State.Should().Be(OverrideState.Proposed);
    }

    [Fact]
    public async Task Revoke_InvalidGuid_FailsBeforeRbac()
    {
        var (svc, _, gate) = NewServices();
        var rbac = new RecordingRbac();

        var output = await OverrideTools.Revoke(
            svc, gate, AccessorFor("user:approver"), rbac,
            "not-a-guid", "reason");

        output.Should().StartWith("policy.override.invalid_argument:");
        rbac.Calls.Should().BeEmpty(
            "a malformed override id has no resource instance to attach the " +
            "RBAC question to; the validation guard fires first");
    }
}
