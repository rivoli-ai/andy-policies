// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Api.Mcp;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Andy.Policies.Tests.Integration.Mcp;

/// <summary>
/// Tests for <see cref="PolicyLifecycleTools"/> (P2.5, #15). Drives the
/// static tool methods directly against a real
/// <see cref="LifecycleTransitionService"/> backed by EF Core InMemory plus a
/// real <see cref="PolicyService"/> for seeding. Verifies the wire contract
/// returned to MCP agents: formatted strings on success, prefixed error
/// codes (RATIONALE_REQUIRED, INVALID_TRANSITION, NOT_FOUND, etc.) on
/// failure, and that the actor-fallback firewall rejects callers without a
/// subject claim.
/// </summary>
public class PolicyLifecycleToolsTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private sealed class NoopDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync<TEvent>(TEvent e, CancellationToken ct = default)
            where TEvent : notnull => Task.CompletedTask;
    }

    /// <summary>
    /// Default-allow RBAC for tests not exercising the authorization
    /// path itself; P7.6 (#64) added <see cref="IRbacChecker"/> to every
    /// mutating MCP-tool signature, so every call site below threads
    /// this through. Authorization-specific behaviour is covered by
    /// <c>OverrideToolsRbacTests</c>.
    /// </summary>
    private static readonly IRbacChecker AllowRbac = McpToolStubs.AllowAllRbac;

    private static (PolicyService policies, LifecycleTransitionService lifecycle, AppDbContext db)
        NewServices()
    {
        var db = NewDb();
        var lifecycle = new LifecycleTransitionService(
            db,
            new RequireNonEmptyRationalePolicy(),
            new NoopDispatcher(),
            TimeProvider.System);
        return (new PolicyService(db), lifecycle, db);
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

    private static CreatePolicyRequest MinimalCreate(string name) => new(
        Name: name,
        Description: null,
        Summary: "summary",
        Enforcement: "Must",
        Severity: "Critical",
        Scopes: Array.Empty<string>(),
        RulesJson: "{}");

    [Fact]
    public async Task Publish_OnDraft_ReturnsActiveDetail()
    {
        var (policies, lifecycle, db) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("publish-tool"), "sam");

        var output = await PolicyLifecycleTools.Publish(
            lifecycle, AccessorFor("agent-1"), AllowRbac,
            draft.PolicyId.ToString(), draft.Id.ToString(), "promote v1");

        output.Should().Contain("State: Active");
        output.Should().Contain($"Id: {draft.Id}");

        var reloaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == draft.Id);
        reloaded.State.Should().Be(LifecycleState.Active);
        reloaded.PublishedBySubjectId.Should().Be("agent-1");
    }

    [Fact]
    public async Task Transition_TargetRetired_FromActive_Succeeds()
    {
        var (policies, lifecycle, _) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("transition-tool"), "sam");
        await PolicyLifecycleTools.Publish(lifecycle, AccessorFor("agent-1"), AllowRbac,
            draft.PolicyId.ToString(), draft.Id.ToString(), "live");

        var output = await PolicyLifecycleTools.Transition(
            lifecycle, AccessorFor("agent-1"), AllowRbac,
            draft.PolicyId.ToString(), draft.Id.ToString(), "Retired", "tomb");

        output.Should().Contain("State: Retired");
    }

    [Fact]
    public async Task Transition_AcceptsCaseInsensitiveTargetState()
    {
        var (policies, lifecycle, _) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("case-insensitive"), "sam");

        var output = await PolicyLifecycleTools.Transition(
            lifecycle, AccessorFor("agent-1"), AllowRbac,
            draft.PolicyId.ToString(), draft.Id.ToString(), "active", "go");

        output.Should().Contain("State: Active");
    }

    [Fact]
    public async Task Transition_RejectsTargetDraft()
    {
        var (policies, lifecycle, _) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("reject-draft"), "sam");

        var output = await PolicyLifecycleTools.Transition(
            lifecycle, AccessorFor("agent-1"), AllowRbac,
            draft.PolicyId.ToString(), draft.Id.ToString(), "Draft", "no");

        output.Should().Contain("Invalid target state");
    }

    [Fact]
    public async Task Transition_RejectsUnknownTargetState()
    {
        var (policies, lifecycle, _) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("unknown-target"), "sam");

        var output = await PolicyLifecycleTools.Transition(
            lifecycle, AccessorFor("agent-1"), AllowRbac,
            draft.PolicyId.ToString(), draft.Id.ToString(), "Unicorn", "?");

        output.Should().Contain("Invalid target state");
    }

    [Fact]
    public async Task Transition_DisallowedTransition_ReturnsInvalidTransition()
    {
        // Draft -> Retired is not in the matrix.
        var (policies, lifecycle, _) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("draft-to-retired"), "sam");

        var output = await PolicyLifecycleTools.Transition(
            lifecycle, AccessorFor("agent-1"), AllowRbac,
            draft.PolicyId.ToString(), draft.Id.ToString(), "Retired", "skip");

        output.Should().StartWith("INVALID_TRANSITION:");
    }

    [Fact]
    public async Task Publish_EmptyRationale_ReturnsRationaleRequired()
    {
        var (policies, lifecycle, _) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("rationale-required"), "sam");

        var output = await PolicyLifecycleTools.Publish(
            lifecycle, AccessorFor("agent-1"), AllowRbac,
            draft.PolicyId.ToString(), draft.Id.ToString(), "  ");

        output.Should().StartWith("RATIONALE_REQUIRED:");
    }

    [Fact]
    public async Task Publish_NoSubjectId_ReturnsAuthRequired_AndDoesNotMutate()
    {
        var (policies, lifecycle, db) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("no-subject"), "sam");

        var output = await PolicyLifecycleTools.Publish(
            lifecycle, AccessorFor(subjectId: null), AllowRbac,
            draft.PolicyId.ToString(), draft.Id.ToString(), "publish");

        output.Should().Contain("Authentication required");
        var reloaded = await db.PolicyVersions.AsNoTracking().FirstAsync(v => v.Id == draft.Id);
        reloaded.State.Should().Be(LifecycleState.Draft);
    }

    [Fact]
    public async Task Publish_InvalidPolicyId_ReturnsErrorString()
    {
        var (_, lifecycle, _) = NewServices();

        var output = await PolicyLifecycleTools.Publish(
            lifecycle, AccessorFor("agent-1"), AllowRbac,
            "not-a-guid", Guid.NewGuid().ToString(), "go");

        output.Should().StartWith("Invalid policy id:");
    }

    [Fact]
    public async Task Publish_UnknownIds_ReturnsNotFound()
    {
        var (_, lifecycle, _) = NewServices();

        var output = await PolicyLifecycleTools.Publish(
            lifecycle, AccessorFor("agent-1"), AllowRbac,
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "go");

        output.Should().StartWith("NOT_FOUND:");
    }

    [Fact]
    public void Matrix_ReturnsTheFourCanonicalRules()
    {
        var (_, lifecycle, _) = NewServices();

        var output = PolicyLifecycleTools.Matrix(lifecycle);

        output.Should().Contain("4 allowed transitions:");
        output.Should().Contain("Draft -> Active (Publish)");
        output.Should().Contain("Active -> WindingDown (WindDown)");
        output.Should().Contain("Active -> Retired (Retire)");
        output.Should().Contain("WindingDown -> Retired (Retire)");
    }
}
