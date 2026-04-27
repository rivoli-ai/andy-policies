// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Decides whether a rationale string is valid for a lifecycle transition
/// (P2.2). The default implementation rejects null/whitespace; P2.4 will
/// replace it with a settings-driven implementation that reads
/// <c>andy.policies.rationaleRequired</c> from andy-settings and relaxes
/// the check accordingly.
/// </summary>
public interface IRationalePolicy
{
    /// <summary>
    /// Validate <paramref name="rationale"/> for the given target transition.
    /// Returns null on success; returns a human-readable error message on
    /// failure (the calling service translates this to a 400-mapped exception).
    /// </summary>
    string? ValidateRationale(string? rationale);
}
