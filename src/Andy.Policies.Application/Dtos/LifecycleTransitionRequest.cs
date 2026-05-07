// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Request payload for the lifecycle transition endpoints (P2.3, story
/// rivoli-ai/andy-policies#13): publish, winding-down, retire. The
/// <c>rationale</c> field is forwarded to <c>ILifecycleTransitionService</c>
/// where <c>IRationalePolicy</c> validates it against the
/// <c>andy.policies.rationaleRequired</c> setting (P2.4 wires the dynamic
/// gate; P2.3 ships with the require-non-empty default).
/// <para>
/// <c>ExpectedRevision</c> (P9 follow-up #194, 2026-05-07) is an optional
/// optimistic-concurrency token. When supplied, the service verifies the
/// loaded version's <c>Revision</c> matches before transitioning; mismatch
/// returns 412. Nullable for backward compat — clients that don't set it
/// fall through to last-write-wins (the existing behavior).
/// </para>
/// </summary>
public record LifecycleTransitionRequest(string? Rationale, uint? ExpectedRevision = null);
