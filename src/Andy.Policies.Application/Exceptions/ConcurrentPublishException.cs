// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown by <c>ILifecycleTransitionService.TransitionAsync</c> when two
/// concurrent <c>Draft -&gt; Active</c> attempts race for the same policy
/// and the unique-partial-index on <c>(PolicyId) WHERE State = 'Active'</c>
/// rejects the loser. API layer maps to HTTP 409 Conflict — the caller
/// should re-read the active version and decide whether to retry.
/// </summary>
public sealed class ConcurrentPublishException : Exception
{
    public Guid PolicyId { get; }

    public ConcurrentPublishException(Guid policyId, Exception inner)
        : base($"Concurrent publish detected for policy {policyId}: another version was activated first.", inner)
    {
        PolicyId = policyId;
    }
}
