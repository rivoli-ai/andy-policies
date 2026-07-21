// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Api.Authorization;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Authorization;

public sealed class ActorSubjectResolverTests
{
    [Fact]
    public void Resolve_UsesCanonicalPrecedenceAndTrimsSubject()
    {
        var principal = Principal(
            (ClaimTypes.NameIdentifier, "  user:name-id  "),
            ("sub", "user:raw-sub"),
            (ClaimTypes.Name, "display name"));

        ActorSubjectResolver.Resolve(principal).Should().Be("user:name-id");
    }

    [Fact]
    public void Resolve_SkipsBlankMappedClaimAndUsesRawSub()
    {
        var principal = Principal(
            (ClaimTypes.NameIdentifier, "  "),
            ("sub", "user:raw-sub"),
            (ClaimTypes.Name, "display name"));

        ActorSubjectResolver.Resolve(principal).Should().Be("user:raw-sub");
    }

    [Fact]
    public void Require_RejectsPrincipalWithoutAnyUsableSubject()
    {
        var act = () => ActorSubjectResolver.Require(Principal(("groups", "operators")));

        act.Should().Throw<MissingActorSubjectException>();
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));
}
