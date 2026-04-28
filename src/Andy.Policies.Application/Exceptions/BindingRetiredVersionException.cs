// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown by <c>IBindingService.CreateAsync</c> when the target
/// <c>PolicyVersion</c> is in <c>LifecycleState.Retired</c> (P3.2, story
/// rivoli-ai/andy-policies#20). API layer maps to HTTP 409 Conflict; gRPC
/// to <c>FailedPrecondition</c>. Active and WindingDown versions are
/// bindable — only Retired refuses new bindings.
/// </summary>
public sealed class BindingRetiredVersionException : ConflictException
{
    public Guid PolicyVersionId { get; }

    public BindingRetiredVersionException(Guid policyVersionId)
        : base($"Cannot create binding: PolicyVersion {policyVersionId} is Retired.")
    {
        PolicyVersionId = policyVersionId;
    }
}
