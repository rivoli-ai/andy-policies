// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown by <c>BindingService.CreateAsync</c> when
/// <c>ITightenOnlyValidator.ValidateCreateAsync</c> rejects the
/// proposed binding because it would loosen a Mandatory binding
/// declared upstream (P4.4, story rivoli-ai/andy-policies#32). API
/// layer maps to HTTP 409 with
/// <c>errorCode = "binding.tighten-only-violation"</c> and surfaces
/// the offending ancestor binding id + scope node id so admins can
/// triage without a follow-up query.
/// </summary>
public sealed class TightenOnlyViolationException : ConflictException
{
    public TightenViolation Violation { get; }

    public TightenOnlyViolationException(TightenViolation violation)
        : base(violation.Reason)
    {
        Violation = violation;
    }
}
