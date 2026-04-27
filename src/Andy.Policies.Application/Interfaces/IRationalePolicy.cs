// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Decides whether a rationale string is valid for a lifecycle transition.
/// P2.4 (#14) wires this to the andy-settings toggle
/// <c>andy.policies.rationaleRequired</c>: when true (default),
/// null/empty/whitespace rationale is rejected; when false, all rationale
/// values pass through. The setting is read from <c>ISettingsSnapshot</c>
/// on every check so a runtime flip from the andy-settings admin UI takes
/// effect on the next transition without restarting the service. If the
/// snapshot has not yet observed the key (cold start, andy-settings briefly
/// unreachable), implementations MUST fail safe to <c>true</c>.
/// </summary>
public interface IRationalePolicy
{
    /// <summary>
    /// True when the live setting <c>andy.policies.rationaleRequired</c> is
    /// on (or unknown — fail-safe). Reads are snapshot-cheap; callers may
    /// poll this for telemetry without rate-limiting.
    /// </summary>
    bool IsRequired { get; }

    /// <summary>
    /// Validate <paramref name="rationale"/> against the live toggle.
    /// Returns null on success; returns a human-readable error message on
    /// failure. The calling service translates a non-null return into
    /// <c>RationaleRequiredException</c>.
    /// </summary>
    string? ValidateRationale(string? rationale);
}
