// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown by <c>ILifecycleTransitionService.TransitionAsync</c> when the
/// requested move is not in the canonical matrix (e.g. <c>Retired -&gt; *</c>,
/// <c>Draft -&gt; WindingDown</c>, any self-transition). API layer maps to
/// HTTP 409 Conflict.
/// </summary>
public sealed class InvalidLifecycleTransitionException : Exception
{
    public LifecycleState From { get; }

    public LifecycleState To { get; }

    public InvalidLifecycleTransitionException(LifecycleState from, LifecycleState to)
        : base($"Lifecycle transition from {from} to {to} is not allowed.")
    {
        From = from;
        To = to;
    }
}
