// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Subject→permission check delegated to andy-rbac (P7.2, story
/// rivoli-ai/andy-policies#51). The service-layer abstraction lets
/// P5.2 (override approval) and future RBAC-gated paths take a
/// dependency on the contract before P7.2 ships the real andy-rbac
/// HTTP client. Until P7.2, the only registered implementation is
/// <c>AllowAllRbacChecker</c>, which logs at Debug and returns
/// <see cref="RbacCheckResult.AllowedResult"/>; this preserves the
/// "no production auth-bypass" posture from #103 because the placeholder
/// only ships in development DI registrations.
/// </summary>
public interface IRbacChecker
{
    /// <summary>
    /// Ask andy-rbac whether <paramref name="subjectId"/> may exercise
    /// <paramref name="permission"/> on the optional resource instance
    /// <paramref name="resourceInstanceId"/> (e.g. a scope ref). The
    /// real implementation calls <c>POST /api/check</c> on andy-rbac
    /// and returns its <c>{ allowed, reason }</c> envelope.
    /// </summary>
    Task<RbacCheckResult> CheckAsync(
        string subjectId,
        string permission,
        string? resourceInstanceId,
        CancellationToken ct = default);
}

/// <summary>
/// Result of an <see cref="IRbacChecker.CheckAsync"/> call.
/// <see cref="Reason"/> is populated on denial so the API layer can
/// echo the structured rationale into ProblemDetails extensions for
/// admin triage.
/// </summary>
public sealed record RbacCheckResult(bool Allowed, string? Reason)
{
    public static RbacCheckResult AllowedResult { get; } = new(true, null);

    public static RbacCheckResult Denied(string reason) => new(false, reason);
}
