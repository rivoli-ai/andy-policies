// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Placeholder <see cref="IRbacChecker"/> until P7.2 (#51) replaces
/// it with the andy-rbac HTTP client. Logs each call at <c>Debug</c>
/// so operators can confirm the call sites are wired correctly even
/// before the real RBAC integration ships.
/// <para>
/// Per <c>CLAUDE.md</c>'s no-auth-bypass rule: this stub is acceptable
/// for development because the JWT layer (Andy Auth) is still
/// required at the API edge. The "allow-all" semantics apply only
/// to the subject→permission check, not to authentication. Once
/// P7.2 lands, the registration in <c>Program.cs</c> swaps to the
/// real client; the interface stays unchanged.
/// </para>
/// </summary>
public sealed class AllowAllRbacChecker : IRbacChecker
{
    private readonly ILogger<AllowAllRbacChecker> _log;

    public AllowAllRbacChecker(ILogger<AllowAllRbacChecker> log)
    {
        _log = log;
    }

    public Task<RbacCheckResult> CheckAsync(
        string subjectId,
        string permission,
        string? resourceInstanceId,
        CancellationToken ct = default)
    {
        _log.LogDebug(
            "RBAC check (allow-all stub until P7.2): subject={Subject} permission={Permission} resource={Resource}",
            subjectId, permission, resourceInstanceId ?? "(none)");
        return Task.FromResult(RbacCheckResult.AllowedResult);
    }
}
