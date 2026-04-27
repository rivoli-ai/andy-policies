// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Default <see cref="IRationalePolicy"/> implementation: rejects null,
/// empty, and whitespace-only rationale strings unconditionally. Stays in
/// place until P2.4 (#14) replaces it with the settings-driven version
/// that consults <c>andy.policies.rationaleRequired</c> from andy-settings.
/// </summary>
public sealed class RequireNonEmptyRationalePolicy : IRationalePolicy
{
    public string? ValidateRationale(string? rationale)
    {
        if (string.IsNullOrWhiteSpace(rationale))
        {
            return "Rationale is required and may not be empty or whitespace.";
        }
        return null;
    }
}
