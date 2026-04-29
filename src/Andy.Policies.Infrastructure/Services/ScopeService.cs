// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// EF-backed implementation of <see cref="IScopeService"/> (P4.2, story
/// rivoli-ai/andy-policies#29). Owns hierarchy CRUD + walk primitives;
/// uses materialized-path indexing (set up in P4.1) so descendant
/// lookups stay on a single LIKE prefix scan and ancestor lookups are
/// a single <c>WHERE Id IN (...)</c> over the parsed path. Cross-
/// provider safe: no recursive CTE, no DateTimeOffset ORDER BY.
/// </summary>
public sealed class ScopeService : IScopeService
{
    private const int MaxRefLength = 512;
    private const int MaxDisplayNameLength = 256;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public ScopeService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<ScopeNodeDto> CreateAsync(CreateScopeNodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refValue = (request.Ref ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(refValue))
        {
            throw new ValidationException("Ref is required and may not be empty or whitespace.");
        }
        if (refValue.Length > MaxRefLength)
        {
            throw new ValidationException(
                $"Ref length {refValue.Length} exceeds the {MaxRefLength}-char limit.");
        }
        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(displayName))
        {
            throw new ValidationException("DisplayName is required and may not be empty or whitespace.");
        }
        if (displayName.Length > MaxDisplayNameLength)
        {
            throw new ValidationException(
                $"DisplayName length {displayName.Length} exceeds the {MaxDisplayNameLength}-char limit.");
        }

        // Type-ladder validation: root must be Org; child must be the
        // immediate successor of its parent (Org→Tenant→Team→Repo→
        // Template→Run). The ordinal-doubles-as-depth contract from
        // P4.1 lets us check both rules with simple int math.
        ScopeNode? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = await _db.ScopeNodes
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == parentId, ct)
                .ConfigureAwait(false)
                ?? throw new NotFoundException($"Parent ScopeNode {parentId} not found.");

            if ((int)request.Type != (int)parent.Type + 1)
            {
                throw new InvalidScopeTypeException(
                    $"Cannot create a {request.Type} under a {parent.Type}; the canonical ladder is " +
                    "Org → Tenant → Team → Repo → Template → Run.");
            }
        }
        else
        {
            if (request.Type != ScopeType.Org)
            {
                throw new InvalidScopeTypeException(
                    $"A root ScopeNode must have Type=Org; got {request.Type}.");
            }
        }

        var now = _clock.GetUtcNow();
        var id = Guid.NewGuid();
        var path = parent is null ? $"/{id}" : $"{parent.MaterializedPath}/{id}";
        var depth = (int)request.Type;

        var node = new ScopeNode
        {
            Id = id,
            ParentId = request.ParentId,
            Type = request.Type,
            Ref = refValue,
            DisplayName = displayName,
            MaterializedPath = path,
            Depth = depth,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ScopeNodes.Add(node);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueRefViolation(ex))
        {
            throw new ScopeRefConflictException(request.Type, refValue, ex);
        }

        return ToDto(node);
    }

    public async Task<ScopeNodeDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var node = await _db.ScopeNodes.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false);
        return node is null ? null : ToDto(node);
    }

    public async Task<ScopeNodeDto?> GetByRefAsync(ScopeType type, string @ref, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(@ref);
        var node = await _db.ScopeNodes.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Type == type && s.Ref == @ref, ct)
            .ConfigureAwait(false);
        return node is null ? null : ToDto(node);
    }

    public async Task<IReadOnlyList<ScopeNodeDto>> ListAsync(ScopeType? type, CancellationToken ct = default)
    {
        var query = _db.ScopeNodes.AsNoTracking();
        if (type is { } t)
        {
            query = query.Where(s => s.Type == t);
        }
        // Sort client-side: Depth then Ref. The composite key never
        // includes DateTimeOffset, so this is SQLite-safe but we keep
        // the materialise-then-sort pattern for parity with the binding
        // services (see #21).
        var rows = await query.ToListAsync(ct).ConfigureAwait(false);
        return rows
            .OrderBy(r => r.Depth)
            .ThenBy(r => r.Ref, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList();
    }

    public async Task<ScopeNodeDto> UpdateAsync(Guid id, UpdateScopeNodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(displayName))
        {
            throw new ValidationException("DisplayName is required and may not be empty or whitespace.");
        }
        if (displayName.Length > MaxDisplayNameLength)
        {
            throw new ValidationException(
                $"DisplayName length {displayName.Length} exceeds the {MaxDisplayNameLength}-char limit.");
        }

        var node = await _db.ScopeNodes
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"ScopeNode {id} not found.");

        node.DisplayName = displayName;
        node.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToDto(node);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var node = await _db.ScopeNodes
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"ScopeNode {id} not found.");

        var childCount = await _db.ScopeNodes
            .CountAsync(s => s.ParentId == id, ct)
            .ConfigureAwait(false);
        if (childCount > 0)
        {
            throw new ScopeHasDescendantsException(id, childCount);
        }

        _db.ScopeNodes.Remove(node);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScopeNodeDto>> GetAncestorsAsync(Guid id, CancellationToken ct = default)
    {
        var node = await _db.ScopeNodes.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"ScopeNode {id} not found.");

        // Parse "/a/b/c/self" into [a, b, c]. The materialized-path
        // contract from P4.1 guarantees the trailing self-id; strip it.
        var ids = node.MaterializedPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => Guid.TryParse(token, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue && g.Value != id)
            .Select(g => g!.Value)
            .ToList();
        if (ids.Count == 0)
        {
            return Array.Empty<ScopeNodeDto>();
        }

        var ancestors = await _db.ScopeNodes.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return ancestors
            .OrderBy(a => a.Depth)
            .Select(ToDto)
            .ToList();
    }

    public async Task<IReadOnlyList<ScopeNodeDto>> GetDescendantsAsync(Guid id, CancellationToken ct = default)
    {
        var node = await _db.ScopeNodes.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"ScopeNode {id} not found.");

        // Subtree = every node whose materialized path starts with
        // "<self.path>/". The trailing slash excludes self and avoids
        // false-positive matches (e.g. /abc-123 vs /abc-1234).
        var prefix = node.MaterializedPath + "/";
        var rows = await _db.ScopeNodes.AsNoTracking()
            .Where(s => s.MaterializedPath.StartsWith(prefix))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows
            .OrderBy(r => r.Depth)
            .ThenBy(r => r.Ref, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList();
    }

    public async Task<IReadOnlyList<ScopeTreeDto>> GetTreeAsync(CancellationToken ct = default)
    {
        var rows = await _db.ScopeNodes.AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return Array.Empty<ScopeTreeDto>();
        }

        // Group nodes by parent. We track roots separately because Guid?
        // is not a valid Dictionary key (the ToDictionary overload
        // requires a notnull TKey).
        var byParent = rows
            .Where(r => r.ParentId is not null)
            .GroupBy(r => r.ParentId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.Ref, StringComparer.Ordinal).ToList());
        var roots = rows
            .Where(r => r.ParentId is null)
            .OrderBy(r => r.Ref, StringComparer.Ordinal)
            .ToList();

        ScopeTreeDto Build(ScopeNode node)
        {
            var children = byParent.TryGetValue(node.Id, out var kids)
                ? kids.Select(Build).ToList()
                : new List<ScopeTreeDto>();
            return new ScopeTreeDto(ToDto(node), children);
        }

        return roots.Select(Build).ToList();
    }

    private static ScopeNodeDto ToDto(ScopeNode node) => new(
        node.Id,
        node.ParentId,
        node.Type,
        node.Ref,
        node.DisplayName,
        node.MaterializedPath,
        node.Depth,
        node.CreatedAt,
        node.UpdatedAt);

    private static bool IsUniqueRefViolation(DbUpdateException ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("23505", StringComparison.Ordinal)
            || msg.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase);
    }
}
