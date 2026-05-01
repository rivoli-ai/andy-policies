// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Settings;

/// <summary>
/// Runtime gate over <c>andy.policies.experimentalOverridesEnabled</c>
/// (P5.4, story rivoli-ai/andy-policies#56). When the toggle is
/// <c>false</c>, override <i>writes</i> (propose, approve, revoke)
/// must be rejected at the surface layer with HTTP 403 / gRPC
/// <c>PERMISSION_DENIED</c> / MCP error envelope <c>override.disabled</c>;
/// reads remain available so consumers can still see existing
/// approved overrides and the resolution algorithm (P4.3) keeps
/// working.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-closed:</b> when the underlying settings snapshot has not
/// observed the key yet (cold start, andy-settings briefly
/// unreachable), <see cref="IsEnabled"/> returns <c>false</c>. This
/// matches the registered default of <c>false</c> and keeps the
/// blast radius of an outage at the safer state.
/// </para>
/// <para>
/// <b>Why a gate primitive rather than gating inside
/// <c>OverrideService</c>?</b> The reaper (P5.3) reuses the same
/// service to drive expiry. Gating at the service layer would force
/// the reaper to bypass the gate, complicating the contract. Gating
/// at the surface layer keeps the service gate-agnostic — the
/// reaper, MCP, REST, and gRPC each apply the gate where it makes
/// sense for their layer.
/// </para>
/// </remarks>
public interface IExperimentalOverridesGate
{
    /// <summary>
    /// <c>true</c> when override writes are currently allowed;
    /// <c>false</c> when they should be rejected with the surface's
    /// equivalent of HTTP 403.
    /// </summary>
    bool IsEnabled { get; }
}
