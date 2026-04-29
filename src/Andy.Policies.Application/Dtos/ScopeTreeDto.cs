// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Recursive tree projection: a <c>ScopeNode</c> plus its full subtree
/// (P4.2, story rivoli-ai/andy-policies#29). Returned by
/// <c>IScopeService.GetTreeAsync</c> and surfaced unchanged by the
/// REST/MCP/gRPC tree endpoints in P4.5 / P4.6.
/// </summary>
public sealed record ScopeTreeDto(ScopeNodeDto Node, IReadOnlyList<ScopeTreeDto> Children);
