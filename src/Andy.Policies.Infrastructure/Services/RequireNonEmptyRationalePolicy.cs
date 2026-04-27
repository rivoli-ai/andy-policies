// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Always-on <see cref="IRationalePolicy"/> implementation: rejects null,
/// empty, and whitespace-only rationale strings unconditionally. Production
/// uses <see cref="AndySettingsRationalePolicy"/> (P2.4, #14) which consults
/// the live <c>andy.policies.rationaleRequired</c> toggle in andy-settings;
/// this class stays as a test convenience that does not depend on a live
/// <c>ISettingsSnapshot</c>, and as the documented behavior shape for the
/// settings-on (default) state.
/// </summary>
public sealed class RequireNonEmptyRationalePolicy : IRationalePolicy
{
    public bool IsRequired => true;

    public string? ValidateRationale(string? rationale)
    {
        if (string.IsNullOrWhiteSpace(rationale))
        {
            return "Rationale is required and may not be empty or whitespace.";
        }
        return null;
    }
}
