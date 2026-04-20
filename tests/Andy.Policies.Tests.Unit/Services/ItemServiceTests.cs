// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

public class ItemServiceTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task CreateAsync_ShouldReturnNewItem()
    {
        using var db = CreateInMemoryDb();
        var service = new ItemService(db);
        var request = new CreateItemRequest("Test Item", "A test description");

        var result = await service.CreateAsync(request, "test-user");

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("Test Item", result.Name);
        Assert.Equal("A test description", result.Description);
        Assert.Equal("Draft", result.Status);
        Assert.Equal("test-user", result.CreatedBy);
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnAllItems()
    {
        using var db = CreateInMemoryDb();
        var service = new ItemService(db);
        await service.CreateAsync(new CreateItemRequest("Item 1", null), "user1");
        await service.CreateAsync(new CreateItemRequest("Item 2", null), "user2");

        var results = (await service.GetAllAsync()).ToList();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetByIdAsync_WhenNotFound_ShouldReturnNull()
    {
        using var db = CreateInMemoryDb();
        var service = new ItemService(db);

        var result = await service.GetByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_ShouldUpdateItem()
    {
        using var db = CreateInMemoryDb();
        var service = new ItemService(db);
        var created = await service.CreateAsync(new CreateItemRequest("Original", null), "user1");

        var updated = await service.UpdateAsync(created.Id, new CreateItemRequest("Updated", "New desc"));

        Assert.NotNull(updated);
        Assert.Equal("Updated", updated!.Name);
        Assert.Equal("New desc", updated.Description);
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveItem()
    {
        using var db = CreateInMemoryDb();
        var service = new ItemService(db);
        var created = await service.CreateAsync(new CreateItemRequest("To Delete", null), "user1");

        var deleted = await service.DeleteAsync(created.Id);
        var found = await service.GetByIdAsync(created.Id);

        Assert.True(deleted);
        Assert.Null(found);
    }

    [Fact]
    public async Task DeleteAsync_WhenNotFound_ShouldReturnFalse()
    {
        using var db = CreateInMemoryDb();
        var service = new ItemService(db);

        var deleted = await service.DeleteAsync(Guid.NewGuid());

        Assert.False(deleted);
    }
}
