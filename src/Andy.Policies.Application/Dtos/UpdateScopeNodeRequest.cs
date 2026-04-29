// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Request payload for <c>IScopeService.UpdateAsync</c> (P4.2). Only
/// the human-readable <c>DisplayName</c> can change post-insert; the
/// hierarchy fields (<c>ParentId</c>, <c>Type</c>, <c>Ref</c>) are
/// immutable per ADR 0004 (re-parenting is out of scope for Epic P4).
/// </summary>
public sealed record UpdateScopeNodeRequest(string DisplayName);
