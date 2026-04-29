// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Application service for the scope hierarchy (P4.2, story
/// rivoli-ai/andy-policies#29). Owns CRUD over <c>ScopeNode</c>, the
/// type-ladder validation (Org→Tenant→Team→Repo→Template→Run), and the
/// walk primitives that <c>BindingResolutionService</c> (P4.3) and
/// <c>BindingValidator</c> (P4.4) consume. REST (P4.5), MCP / gRPC /
/// CLI (P4.6) all delegate here — surfaces never re-implement
/// hierarchy logic.
/// </summary>
public interface IScopeService
{
    Task<ScopeNodeDto> CreateAsync(CreateScopeNodeRequest request, CancellationToken ct = default);

    Task<ScopeNodeDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<ScopeNodeDto?> GetByRefAsync(ScopeType type, string @ref, CancellationToken ct = default);

    Task<IReadOnlyList<ScopeNodeDto>> ListAsync(ScopeType? type, CancellationToken ct = default);

    Task<ScopeNodeDto> UpdateAsync(Guid id, UpdateScopeNodeRequest request, CancellationToken ct = default);

    /// <summary>Hard-delete a leaf node. Throws
    /// <c>ScopeHasDescendantsException</c> when the node still has
    /// children.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Walk-up: returns every ancestor of <paramref name="id"/> ordered
    /// root-first (depth ascending), excluding self. Empty list for a
    /// root node. Throws <c>NotFoundException</c> when
    /// <paramref name="id"/> does not exist.
    /// </summary>
    Task<IReadOnlyList<ScopeNodeDto>> GetAncestorsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Walk-down: returns every descendant of <paramref name="id"/>
    /// ordered by depth then ref, excluding self. Throws
    /// <c>NotFoundException</c> when <paramref name="id"/> does not exist.
    /// </summary>
    Task<IReadOnlyList<ScopeNodeDto>> GetDescendantsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Build the full forest as nested <c>ScopeTreeDto</c>s. The
    /// returned list contains one entry per root node; an empty
    /// catalogue returns an empty list.
    /// </summary>
    Task<IReadOnlyList<ScopeTreeDto>> GetTreeAsync(CancellationToken ct = default);
}
