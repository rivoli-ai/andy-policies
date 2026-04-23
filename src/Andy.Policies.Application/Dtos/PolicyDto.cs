// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Stable identity of a policy plus summary metadata about its version history.
/// <see cref="ActiveVersionId"/> resolves per P1 rule "highest <c>Version</c> with
/// <c>State != Draft</c>"; P2 tightens to <c>State == Active</c>. Null while every
/// version is still a Draft.
/// </summary>
public record PolicyDto(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    string CreatedBySubjectId,
    int VersionCount,
    Guid? ActiveVersionId);
