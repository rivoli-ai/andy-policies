// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Enums;

/// <summary>
/// How strongly a <see cref="Entities.Binding"/> applies to its target.
/// Consumers (Conductor's ActionBus, andy-tasks per-task gates) interpret
/// the strength to decide whether to block or warn — this service stores
/// the value but does not enforce it (see Epic P3 Non-goals).
/// Persisted as <c>int</c>; values 1 / 2 are load-bearing on disk.
/// </summary>
public enum BindStrength
{
    /// <summary>The binding's policy must apply; consumers reject runs that violate it.</summary>
    Mandatory = 1,

    /// <summary>The binding's policy applies as guidance; consumers warn but do not block.</summary>
    Recommended = 2,
}
