// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Request payload for <c>IOverrideService.RevokeAsync</c> (P5.2).
/// The reason is required and persisted to
/// <c>Override.RevocationReason</c>; reaper-driven expiry
/// (<c>Expired</c>) leaves the column null.
/// </summary>
public sealed record RevokeOverrideRequest(string RevocationReason);
