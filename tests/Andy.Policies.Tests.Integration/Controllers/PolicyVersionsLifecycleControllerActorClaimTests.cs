// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Api.Controllers;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// Direct controller-level tests for the actor-fallback firewall mandated by
/// P2.3 (story rivoli-ai/andy-policies#13 §"Actor-fallback firewall"). The
/// controller must never write a fallback subject id like <c>"anonymous"</c>
/// into <c>ILifecycleTransitionService.TransitionAsync</c>; if neither
/// <c>NameIdentifier</c> (production JWT <c>sub</c>) nor <c>Name</c> (test
/// principal) is present it must short-circuit to 401 before the service
/// runs. This test exercises the controller in-process with a fake principal —
/// the HTTP integration tests cover the happy path through TestAuthHandler.
/// </summary>
public class PolicyVersionsLifecycleControllerActorClaimTests
{
    private static (PolicyVersionsLifecycleController controller, RecordingTransitionService stub) Build(
        ClaimsPrincipal principal)
    {
        var stub = new RecordingTransitionService();
        var controller = new PolicyVersionsLifecycleController(stub)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
        return (controller, stub);
    }

    [Fact]
    public async Task Publish_WithNoSubjectClaims_Returns401_AndDoesNotCallService()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var (controller, stub) = Build(anonymous);

        var result = await controller.Publish(
            Guid.NewGuid(), Guid.NewGuid(), new LifecycleTransitionRequest("ship"), CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedResult>();
        stub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Publish_WithNameIdentifier_PassesSubjectIdToService()
    {
        var jwt = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-42"),
        }, authenticationType: "Bearer"));
        var (controller, stub) = Build(jwt);

        await controller.Publish(
            Guid.NewGuid(), Guid.NewGuid(), new LifecycleTransitionRequest("ship"), CancellationToken.None);

        stub.Calls.Should().ContainSingle().Which.ActorSubjectId.Should().Be("user-42");
    }

    [Fact]
    public async Task Publish_WithOnlyNameClaim_FallsBackToName_ForTestSchemes()
    {
        // TestAuthHandler in the integration suite issues only a Name claim
        // (TestSubjectId = "test-user"). The controller must accept that path
        // so [Authorize]-gated integration tests don't depend on a separate
        // production-JWT-style claim shape.
        var testPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "test-user"),
        }, authenticationType: "Test"));
        var (controller, stub) = Build(testPrincipal);

        await controller.Publish(
            Guid.NewGuid(), Guid.NewGuid(), new LifecycleTransitionRequest("ship"), CancellationToken.None);

        stub.Calls.Should().ContainSingle().Which.ActorSubjectId.Should().Be("test-user");
    }

    [Fact]
    public async Task Retire_WithNoSubjectClaims_Returns401()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var (controller, stub) = Build(anonymous);

        var result = await controller.Retire(
            Guid.NewGuid(), Guid.NewGuid(), new LifecycleTransitionRequest("tomb"), CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedResult>();
        stub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WindDown_WithNoSubjectClaims_Returns401()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var (controller, stub) = Build(anonymous);

        var result = await controller.WindDown(
            Guid.NewGuid(), Guid.NewGuid(), new LifecycleTransitionRequest("sunset"), CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedResult>();
        stub.Calls.Should().BeEmpty();
    }

    private sealed record RecordedCall(
        Guid PolicyId, Guid VersionId, LifecycleState Target,
        string Rationale, string ActorSubjectId);

    private sealed class RecordingTransitionService : ILifecycleTransitionService
    {
        public List<RecordedCall> Calls { get; } = new();

        public bool IsTransitionAllowed(LifecycleState from, LifecycleState to) => true;

        public IReadOnlyList<LifecycleTransitionRule> GetMatrix() => Array.Empty<LifecycleTransitionRule>();

        public Task<PolicyVersionDto> TransitionAsync(
            Guid policyId, Guid versionId, LifecycleState target,
            string rationale, string actorSubjectId, CancellationToken ct = default)
        {
            Calls.Add(new RecordedCall(policyId, versionId, target, rationale, actorSubjectId));
            return Task.FromResult(new PolicyVersionDto(
                versionId, policyId, 1, target.ToString(),
                "MUST", "critical", Array.Empty<string>(),
                "summary", "{}", DateTimeOffset.UtcNow, actorSubjectId, actorSubjectId));
        }
    }
}
