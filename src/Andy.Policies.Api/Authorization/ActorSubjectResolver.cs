// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;

namespace Andy.Policies.Api.Authorization;

/// <summary>
/// Canonical subject extraction for every authenticated API surface.
/// The JWT subject is stable; display-name claims are only a legacy
/// fallback for test and older identity providers.
/// </summary>
public static class ActorSubjectResolver
{
    public static string? Resolve(ClaimsPrincipal? user)
    {
        if (user is null) return null;
        var candidates = new[]
        {
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            user.FindFirstValue("sub"),
            user.Identity?.Name,
        };
        return candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    public static string Require(ClaimsPrincipal? user) =>
        Resolve(user) ?? throw new MissingActorSubjectException();
}

/// <summary>Authenticated principal did not contain a stable subject.</summary>
public sealed class MissingActorSubjectException : Exception
{
    public MissingActorSubjectException()
        : base("Authentication succeeded but the caller has no resolvable subject claim.")
    {
    }
}
