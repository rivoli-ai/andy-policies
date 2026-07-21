// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Authorization;
using Andy.Policies.Api.Mcp.Authorization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Andy.Policies.Api.Mcp;

[McpServerToolType]
public static class ServiceTools
{
    [McpServerTool, Description("List all items")]
    [RbacGuard("andy-policies:policy:read")]
    public static async Task<string> ListItems(
        IItemService itemService,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        CancellationToken ct = default)
    {
        var denial = await McpRbacGuard.GetDenialAsync(
            rbac, httpContext, "andy-policies:policy:read", "item", null, ct);
        if (denial is not null) return denial;
        var items = await itemService.GetAllAsync(ct);
        if (!items.Any())
            return "No items found.";

        var list = items.Select(i => $"- {i.Name} ({i.Id}): {i.Status}");
        return $"Items:\n{string.Join("\n", list)}";
    }

    [McpServerTool, Description("Get details of a specific item by ID")]
    [RbacGuard("andy-policies:policy:read")]
    public static async Task<string> GetItem(
        IItemService itemService,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        [Description("The item ID (GUID)")] string itemId,
        CancellationToken ct = default)
    {
        var denial = await McpRbacGuard.GetDenialAsync(
            rbac, httpContext, "andy-policies:policy:read", "item", itemId, ct);
        if (denial is not null) return denial;
        var item = await itemService.GetByIdAsync(Guid.Parse(itemId), ct);
        if (item is null)
            return $"Item {itemId} not found.";

        return $"Item: {item.Name}\nDescription: {item.Description ?? "(none)"}\nStatus: {item.Status}\nCreated: {item.CreatedAt:u}\nBy: {item.CreatedBy}";
    }

    [McpServerTool, Description("Create a new item")]
    [RbacGuard("andy-policies:policy:author")]
    public static async Task<string> CreateItem(
        IItemService itemService,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        [Description("Name of the item")] string name,
        [Description("Optional description")] string? description = null,
        [Description("Reason recorded in the audit chain")] string? rationale = null,
        CancellationToken ct = default)
    {
        var denial = await McpRbacGuard.GetDenialAsync(
            rbac, httpContext, "andy-policies:policy:author", "item", null, ct);
        if (denial is not null) return denial;
        var request = new CreateItemRequest(name, description, rationale);
        var actor = ActorSubjectResolver.Require(httpContext.HttpContext?.User);
        var item = await itemService.CreateAsync(request, actor, ct);
        return $"Created item: {item.Name} ({item.Id})";
    }

    [McpServerTool, Description("Delete an item by ID")]
    [RbacGuard("andy-policies:policy:author")]
    public static async Task<string> DeleteItem(
        IItemService itemService,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        [Description("The item ID (GUID)")] string itemId,
        [Description("Reason recorded in the audit chain")] string? rationale = null,
        CancellationToken ct = default)
    {
        var denial = await McpRbacGuard.GetDenialAsync(
            rbac, httpContext, "andy-policies:policy:author", "item", itemId, ct);
        if (denial is not null) return denial;
        var actor = ActorSubjectResolver.Require(httpContext.HttpContext?.User);
        var deleted = await itemService.DeleteAsync(Guid.Parse(itemId), actor, rationale, ct);
        return deleted ? $"Item {itemId} deleted." : $"Item {itemId} not found.";
    }
}
