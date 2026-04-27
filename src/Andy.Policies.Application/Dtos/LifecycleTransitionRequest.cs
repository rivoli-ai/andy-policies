// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Request payload for the lifecycle transition endpoints (P2.3, story
/// rivoli-ai/andy-policies#13): publish, winding-down, retire. The single
/// <c>rationale</c> field is forwarded to <c>ILifecycleTransitionService</c>
/// where <c>IRationalePolicy</c> validates it against the
/// <c>andy.policies.rationaleRequired</c> setting (P2.4 wires the dynamic
/// gate; P2.3 ships with the require-non-empty default).
/// </summary>
public record LifecycleTransitionRequest(string? Rationale);
