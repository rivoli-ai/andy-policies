// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Optional filter for <c>IOverrideService.ListAsync</c> (P5.2). All
/// fields are nullable; null means "no filter on this column". The
/// composite <c>(ScopeKind, ScopeRef, State)</c> index from P5.1
/// covers the common shapes (state-by-scope, all-active, etc.).
/// </summary>
public sealed record OverrideListFilter(
    OverrideState? State = null,
    OverrideScopeKind? ScopeKind = null,
    string? ScopeRef = null,
    Guid? PolicyVersionId = null);
