// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Services;

public class ItemService : IItemService
{
    private readonly AppDbContext _db;

    public ItemService(AppDbContext db)
    {
        _db = db;
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
        return ToDto(item);
    }

    public async Task<ItemDto?> UpdateAsync(Guid id, CreateItemRequest request, CancellationToken ct = default)
    {
        var item = await _db.Items.FindAsync(new object[] { id }, ct);
        if (item is null) return null;

        item.Name = request.Name;
        item.Description = request.Description;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var item = await _db.Items.FindAsync(new object[] { id }, ct);
        if (item is null) return false;

        _db.Items.Remove(item);
        await _db.SaveChangesAsync(ct);
        return true;
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
