// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Services;

public class ItemService : IItemService
{
    private readonly AppDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IRationalePolicy _rationale;

    public ItemService(AppDbContext db, IAuditWriter audit, IRationalePolicy rationale)
    {
        _db = db;
        _audit = audit;
        _rationale = rationale;
    }

    public async Task<IEnumerable<ItemDto>> GetAllAsync(CancellationToken ct = default)
    {
        var items = await _db.Items.ToListAsync(ct);
        return items
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => ToDto(i))
            .ToList();
    }

    public async Task<ItemDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var item = await _db.Items.FindAsync(new object[] { id }, ct);
        return item is null ? null : ToDto(item);
    }

    public async Task<ItemDto> CreateAsync(CreateItemRequest request, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ValidateRationale(request.Rationale);
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            Status = ItemStatus.Draft,
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Items.Add(item);
        await _db.SaveChangesAsync(ct);
        await _audit.AppendAsync("item.created", item.Id, userId, request.Rationale, ct)
            .ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return ToDto(item);
    }

    public async Task<ItemDto?> UpdateAsync(
        Guid id, CreateItemRequest request, string actorSubjectId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(actorSubjectId);
        ValidateRationale(request.Rationale);
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var item = await _db.Items.FindAsync(new object[] { id }, ct);
        if (item is null) return null;

        item.Name = request.Name;
        item.Description = request.Description;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        await _audit.AppendAsync("item.updated", item.Id, actorSubjectId, request.Rationale, ct)
            .ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return ToDto(item);
    }

    public async Task<bool> DeleteAsync(
        Guid id, string actorSubjectId, string? rationale, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(actorSubjectId);
        ValidateRationale(rationale);
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var item = await _db.Items.FindAsync(new object[] { id }, ct);
        if (item is null) return false;

        _db.Items.Remove(item);
        await _db.SaveChangesAsync(ct);
        await _audit.AppendAsync("item.deleted", item.Id, actorSubjectId, rationale, ct)
            .ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private void ValidateRationale(string? rationale)
    {
        var error = _rationale.ValidateRationale(rationale);
        if (error is not null) throw new RationaleRequiredException(error);
    }

    private static ItemDto ToDto(Item item) => new(
        item.Id,
        item.Name,
        item.Description,
        item.Status.ToString(),
        item.CreatedBy,
        item.CreatedAt,
        item.UpdatedAt);
}
