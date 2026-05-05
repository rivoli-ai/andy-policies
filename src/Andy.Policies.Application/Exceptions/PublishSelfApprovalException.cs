// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown by <c>LifecycleTransitionService</c> when the publish actor's
/// subject id matches the version's <c>ProposerSubjectId</c> (P7.3,
/// story rivoli-ai/andy-policies#55). A domain invariant — fires even
/// when the actor holds both <c>andy-policies:policy:author</c> and
/// <c>andy-policies:policy:publish</c>. Distinct from
/// <see cref="SelfApprovalException"/> (which guards override approval).
/// API layer maps to HTTP 403 with
/// <c>errorCode = "policy.publish_self_approval_forbidden"</c>.
/// </summary>
public sealed class PublishSelfApprovalException : Exception
{
    public Guid PolicyVersionId { get; }

    public string SubjectId { get; }

    public PublishSelfApprovalException(Guid policyVersionId, string subjectId)
        : base($"Subject '{subjectId}' cannot publish their own draft (version {policyVersionId}).")
    {
        PolicyVersionId = policyVersionId;
        SubjectId = subjectId;
    }
}
