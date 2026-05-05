// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Subject→permission check delegated to andy-rbac (P7.2, story
/// rivoli-ai/andy-policies#51). Service-layer call sites and ASP.NET
/// authorization handlers (P7.4) take a dependency on this contract;
/// the production implementation
/// (<c>HttpRbacChecker</c>) calls <c>POST /api/check</c> on andy-rbac
/// with a 60s in-memory cache and a fail-closed default on transport
/// or timeout errors. Tests substitute their own stubs via DI.
/// </summary>
public interface IRbacChecker
{
    /// <summary>
    /// Ask andy-rbac whether <paramref name="subjectId"/> may exercise
    /// <paramref name="permissionCode"/> on the optional resource
    /// instance <paramref name="resourceInstanceId"/> (e.g. a scope ref).
    /// <paramref name="groups"/> is the JWT <c>groups</c> claim lifted
    /// by the caller — this service does not perform identity
    /// extraction or group resolution.
    /// </summary>
    Task<RbacDecision> CheckAsync(
        string subjectId,
        string permissionCode,
        IReadOnlyList<string> groups,
        string? resourceInstanceId,
        CancellationToken ct);
}

/// <summary>
/// Result of an <see cref="IRbacChecker.CheckAsync"/> call.
/// <see cref="Reason"/> is populated on denial so the API layer can
/// echo the structured rationale into ProblemDetails extensions for
/// admin triage; on allow it carries the matched role / permission
/// code from andy-rbac (e.g. <c>"role:approver"</c>).
/// </summary>
public sealed record RbacDecision(bool Allowed, string Reason)
{
    public static RbacDecision Allow(string reason) => new(true, reason);

    public static RbacDecision Deny(string reason) => new(false, reason);
}
