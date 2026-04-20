// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Andy.Policies.Api.Mcp;

[McpServerToolType]
public static class ServiceTools
{
    [McpServerTool, Description("List all items")]
    public static async Task<string> ListItems(IItemService itemService)
    {
        var items = await itemService.GetAllAsync();
        if (!items.Any())
            return "No items found.";

        var list = items.Select(i => $"- {i.Name} ({i.Id}): {i.Status}");
        return $"Items:\n{string.Join("\n", list)}";
    }

    [McpServerTool, Description("Get details of a specific item by ID")]
    public static async Task<string> GetItem(
        IItemService itemService,
        [Description("The item ID (GUID)")] string itemId)
    {
        var item = await itemService.GetByIdAsync(Guid.Parse(itemId));
        if (item is null)
            return $"Item {itemId} not found.";

        return $"Item: {item.Name}\nDescription: {item.Description ?? "(none)"}\nStatus: {item.Status}\nCreated: {item.CreatedAt:u}\nBy: {item.CreatedBy}";
    }

    [McpServerTool, Description("Create a new item")]
    public static async Task<string> CreateItem(
        IItemService itemService,
        [Description("Name of the item")] string name,
        [Description("Optional description")] string? description = null)
    {
        var request = new CreateItemRequest(name, description);
        var item = await itemService.CreateAsync(request, "mcp-client");
        return $"Created item: {item.Name} ({item.Id})";
    }

    [McpServerTool, Description("Delete an item by ID")]
    public static async Task<string> DeleteItem(
        IItemService itemService,
        [Description("The item ID (GUID)")] string itemId)
    {
        var deleted = await itemService.DeleteAsync(Guid.Parse(itemId));
        return deleted ? $"Item {itemId} deleted." : $"Item {itemId} not found.";
    }
}
