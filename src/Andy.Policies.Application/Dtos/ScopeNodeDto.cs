// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Wire-format projection of a <c>ScopeNode</c> (P4.2, story
/// rivoli-ai/andy-policies#29). Surface controllers (REST P4.5, MCP /
/// gRPC / CLI in P4.6) emit this shape directly so wire behaviour stays
/// uniform across surfaces.
/// </summary>
public sealed record ScopeNodeDto(
    Guid Id,
    Guid? ParentId,
    ScopeType Type,
    string Ref,
    string DisplayName,
    string MaterializedPath,
    int Depth,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
