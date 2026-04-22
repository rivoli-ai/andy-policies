// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Enums;

/// <summary>
/// Lifecycle state of a <see cref="Entities.PolicyVersion"/>.
/// Full semantics and transitions are pinned in ADR 0002 (docs/adr/0002-lifecycle-states.md).
/// P1.1 ships all four values so P2 implementation does not need to alter the enum.
/// </summary>
public enum LifecycleState
{
    /// <summary>The only mutable state. Not resolvable by consumers; not bindable.</summary>
    Draft = 0,

    /// <summary>Immutable. Canonical for consumers. Exactly one <c>Active</c> per policy at a time.</summary>
    Active = 1,

    /// <summary>Immutable. Still resolvable for legacy consumer pins; refuses new bindings.</summary>
    WindingDown = 2,

    /// <summary>Immutable. Tombstoned. Resolution returns 410 unless explicitly requested.</summary>
    Retired = 3,
}
