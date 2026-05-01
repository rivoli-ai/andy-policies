// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Api.Filters;

/// <summary>
/// Opts an action (or whole controller) out of
/// <see cref="RationaleRequiredFilter"/> (P6.4, story
/// rivoli-ai/andy-policies#44). Reserved for endpoints whose
/// payloads legitimately have no rationale concept (e.g. system
/// triggers, agent automation hooks, internal diagnostics);
/// every other mutating endpoint must carry a rationale when
/// <c>andy.policies.rationaleRequired</c> is on.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class,
    AllowMultiple = false, Inherited = true)]
public sealed class SkipRationaleCheckAttribute : Attribute
{
}
