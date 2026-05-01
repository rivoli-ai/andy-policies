// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown when <c>IRbacChecker.CheckAsync</c> returns <c>Allowed = false</c>.
/// Distinct from <see cref="SelfApprovalException"/>: this fires when
/// the subject lacks the relevant permission (or the permission lookup
/// failed closed). API layer maps to HTTP 403 with
/// <c>errorCode = "rbac.denied"</c> and surfaces the andy-rbac reason
/// in ProblemDetails extensions for admin triage.
/// </summary>
public sealed class RbacDeniedException : Exception
{
    public string SubjectId { get; }

    public string Permission { get; }

    public string? ResourceInstanceId { get; }

    public string? Reason { get; }

    public RbacDeniedException(string subjectId, string permission, string? resourceInstanceId, string? reason)
        : base($"Subject '{subjectId}' is not permitted to '{permission}'" +
               (resourceInstanceId is null ? string.Empty : $" on '{resourceInstanceId}'") +
               (reason is null ? "." : $": {reason}"))
    {
        SubjectId = subjectId;
        Permission = permission;
        ResourceInstanceId = resourceInstanceId;
        Reason = reason;
    }
}
