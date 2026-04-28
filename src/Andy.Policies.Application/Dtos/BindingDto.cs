// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Wire-format projection of a <c>Binding</c> (P3.2, story
/// rivoli-ai/andy-policies#20). Surface controllers (REST, MCP, gRPC, CLI)
/// emit this shape directly so wire behavior stays uniform.
/// <see cref="DeletedAt"/> is non-null when the binding has been
/// soft-deleted; service-layer reads filter tombstoned rows by default.
/// </summary>
public sealed record BindingDto(
    Guid Id,
    Guid PolicyVersionId,
    BindingTargetType TargetType,
    string TargetRef,
    BindStrength BindStrength,
    DateTimeOffset CreatedAt,
    string CreatedBySubjectId,
    DateTimeOffset? DeletedAt,
    string? DeletedBySubjectId);
