// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Api.Authorization;
using Andy.Policies.Application.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Authorization;

/// <summary>
/// P7.4 (#57) — exercises <see cref="RbacAuthorizationHandler"/> over a
/// stub <see cref="IRbacChecker"/> and a synthetic
/// <see cref="HttpContext"/>. No webhost is needed; these are unit-shaped
/// tests living in the integration project because the handler ships in
/// <c>Andy.Policies.Api</c>, which only the integration project
/// references.
/// </summary>
public class RbacAuthorizationHandlerTests
{
    private const string Permission = "andy-policies:policy:publish";

    private static (RbacAuthorizationHandler handler, RecordingChecker rbac, DefaultHttpContext ctx)
        Build()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.RouteValues["id"] = "11111111-1111-1111-1111-111111111111";
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        var rbac = new RecordingChecker();
        var handler = new RbacAuthorizationHandler(
            rbac, accessor, NullLogger<RbacAuthorizationHandler>.Instance);
        return (handler, rbac, ctx);
    }

    private static AuthorizationHandlerContext NewAuthContext(
        DefaultHttpContext httpCtx, ClaimsPrincipal user, RbacRequirement requirement)
    {
        httpCtx.User = user;
        return new AuthorizationHandlerContext(new[] { requirement }, user, resource: null);
    }

    [Fact]
    public async Task Allow_OnRbacApprove_SucceedsRequirement()
    {
        var (handler, rbac, ctx) = Build();
        rbac.Decision = new RbacDecision(true, "role:approver");
        var user = NewUser(("sub", "user:alice"));
        var auth = NewAuthContext(ctx, user, new RbacRequirement(Permission));

        await handler.HandleAsync(auth);

        auth.HasSucceeded.Should().BeTrue();
        rbac.Calls.Should().HaveCount(1);
        rbac.Calls[0].SubjectId.Should().Be("user:alice");
        rbac.Calls[0].Permission.Should().Be(Permission);
        rbac.Calls[0].ResourceInstance.Should().Be("policy:11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public async Task Deny_OnRbacReject_DoesNotSucceedRequirement()
    {
        var (handler, rbac, ctx) = Build();
        rbac.Decision = new RbacDecision(false, "no-permission");
        var user = NewUser(("sub", "user:bob"));
        var auth = NewAuthContext(ctx, user, new RbacRequirement(Permission));

        await handler.HandleAsync(auth);

        auth.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task FailClosed_TreatedAsDeny()
    {
        var (handler, rbac, ctx) = Build();
        rbac.Decision = new RbacDecision(false, "rbac-unreachable: fail-closed default");
        var user = NewUser(("sub", "user:alice"));
        var auth = NewAuthContext(ctx, user, new RbacRequirement(Permission));

        await handler.HandleAsync(auth);

        auth.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingSubjectClaim_ReturnsWithoutSucceedingOrCallingRbac()
    {
        var (handler, rbac, ctx) = Build();
        var emptyUser = new ClaimsPrincipal(new ClaimsIdentity());
        var auth = NewAuthContext(ctx, emptyUser, new RbacRequirement(Permission));

        await handler.HandleAsync(auth);

        auth.HasSucceeded.Should().BeFalse();
        rbac.Calls.Should().BeEmpty(
            "no subject means no rbac call — let the framework return 403");
    }

    [Fact]
    public async Task SubjectId_PrefersNameIdentifier_ThenSub_ThenIdentityName()
    {
        var (handler, rbac, ctx) = Build();
        rbac.Decision = new RbacDecision(true, "ok");

        // NameIdentifier wins.
        var user1 = NewUser(
            (ClaimTypes.NameIdentifier, "from-nameid"),
            ("sub", "from-sub"),
            (ClaimTypes.Name, "from-name"));
        await handler.HandleAsync(NewAuthContext(ctx, user1, new RbacRequirement(Permission)));
        rbac.Calls[^1].SubjectId.Should().Be("from-nameid");

        // sub wins when NameIdentifier absent.
        var user2 = NewUser(("sub", "from-sub"), (ClaimTypes.Name, "from-name"));
        await handler.HandleAsync(NewAuthContext(ctx, user2, new RbacRequirement(Permission)));
        rbac.Calls[^1].SubjectId.Should().Be("from-sub");

        // Identity.Name fallback when neither claim is present.
        var user3 = NewUser((ClaimTypes.Name, "from-name"));
        await handler.HandleAsync(NewAuthContext(ctx, user3, new RbacRequirement(Permission)));
        rbac.Calls[^1].SubjectId.Should().Be("from-name");
    }

    [Fact]
    public async Task Groups_ArePassedThroughToRbac()
    {
        var (handler, rbac, ctx) = Build();
        rbac.Decision = new RbacDecision(true, "ok");
        var user = NewUser(
            ("sub", "user:alice"),
            ("groups", "team:authors"),
            ("groups", "team:eu"));
        var auth = NewAuthContext(ctx, user, new RbacRequirement(Permission));

        await handler.HandleAsync(auth);

        auth.HasSucceeded.Should().BeTrue();
        rbac.Calls[0].Groups.Should().BeEquivalentTo(new[] { "team:authors", "team:eu" });
    }

    [Fact]
    public async Task NoRouteId_PassesNullResourceInstance()
    {
        var ctx = new DefaultHttpContext();
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        var rbac = new RecordingChecker { Decision = new RbacDecision(true, "ok") };
        var handler = new RbacAuthorizationHandler(
            rbac, accessor, NullLogger<RbacAuthorizationHandler>.Instance);
        var user = NewUser(("sub", "user:alice"));
        var auth = NewAuthContext(ctx, user,
            new RbacRequirement("andy-policies:policy:read"));

        await handler.HandleAsync(auth);

        rbac.Calls[0].ResourceInstance.Should().BeNull();
    }

    private static ClaimsPrincipal NewUser(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(authenticationType: "Test");
        foreach (var (type, value) in claims)
        {
            identity.AddClaim(new Claim(type, value));
        }
        return new ClaimsPrincipal(identity);
    }

    private sealed class RecordingChecker : IRbacChecker
    {
        public RbacDecision Decision { get; set; } = new(true, "default-allow");

        public List<(string SubjectId, string Permission, IReadOnlyList<string> Groups, string? ResourceInstance)>
            Calls { get; } = new();

        public Task<RbacDecision> CheckAsync(
            string subjectId, string permissionCode, IReadOnlyList<string> groups,
            string? resourceInstanceId, CancellationToken ct)
        {
            Calls.Add((subjectId, permissionCode, groups, resourceInstanceId));
            return Task.FromResult(Decision);
        }
    }
}
