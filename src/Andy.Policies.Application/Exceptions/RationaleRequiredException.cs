// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown by <c>ILifecycleTransitionService.TransitionAsync</c> when the
/// caller's rationale is null/empty/whitespace and the live setting
/// <c>andy.policies.rationaleRequired</c> is on (P2.4, #14). Distinct from
/// <see cref="ValidationException"/> so the API layer can emit a typed
/// <c>ProblemDetails</c> with <c>type=/problems/rationale-required</c> and
/// <c>errors.rationale</c> populated. Inherits from <see cref="ValidationException"/>
/// so existing 400-mapping and unit tests that check for the base type keep
/// working without churn.
/// </summary>
public sealed class RationaleRequiredException : ValidationException
{
    public RationaleRequiredException(string message) : base(message) { }

    public RationaleRequiredException(string message, Exception inner) : base(message, inner) { }
}
