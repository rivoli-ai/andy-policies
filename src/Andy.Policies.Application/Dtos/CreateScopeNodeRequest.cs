// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Request payload for <c>IScopeService.CreateAsync</c> (P4.2, story
/// rivoli-ai/andy-policies#29). <see cref="ParentId"/> is null for a
/// root <see cref="ScopeType.Org"/> node; non-null for any other type.
/// The service enforces the canonical Org→Tenant→Team→Repo→Template→Run
/// ladder.
/// </summary>
public sealed record CreateScopeNodeRequest(
    Guid? ParentId,
    ScopeType Type,
    string Ref,
    string DisplayName);
