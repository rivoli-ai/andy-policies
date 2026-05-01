// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown by <c>IOverrideService.ApproveAsync</c> when the approver's
/// subject id matches the override's proposer (P5.2, story
/// rivoli-ai/andy-policies#52). Distinct from <see cref="RbacDeniedException"/>
/// because the rejection happens *before* the RBAC delegation —
/// self-approval is forbidden even if the approver has the
/// <c>andy-policies:override:approve</c> permission. API layer maps
/// to HTTP 403 with <c>errorCode = "override.self_approval_forbidden"</c>.
/// </summary>
public sealed class SelfApprovalException : Exception
{
    public Guid OverrideId { get; }

    public string SubjectId { get; }

    public SelfApprovalException(Guid overrideId, string subjectId)
        : base($"Subject '{subjectId}' cannot approve their own override {overrideId}.")
    {
        OverrideId = overrideId;
        SubjectId = subjectId;
    }
}
