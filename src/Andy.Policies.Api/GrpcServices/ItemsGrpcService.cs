// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Api.Protos;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace Andy.Policies.Api.GrpcServices;

[Authorize]
public class ItemsGrpcService : Protos.ItemsService.ItemsServiceBase
{
    private readonly IItemService _itemService;

    public ItemsGrpcService(IItemService itemService)
    {
        _itemService = itemService;
    }

    public override async Task<GetAllItemsResponse> GetAll(GetAllItemsRequest request, ServerCallContext context)
    {
        var items = await _itemService.GetAllAsync(context.CancellationToken);
        var response = new GetAllItemsResponse();
        response.Items.AddRange(items.Select(ToMessage));
        return response;
    }

    public override async Task<ItemResponse> GetById(GetItemByIdRequest request, ServerCallContext context)
    {
        var item = await _itemService.GetByIdAsync(Guid.Parse(request.Id), context.CancellationToken);
        if (item is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Item {request.Id} not found"));

        return new ItemResponse { Item = ToMessage(item) };
    }

    public override async Task<ItemResponse> Create(CreateItemGrpcRequest request, ServerCallContext context)
    {
        var userId = context.GetHttpContext().User.Identity?.Name ?? "grpc-client";
        var createRequest = new CreateItemRequest(request.Name, request.Description);
        var item = await _itemService.CreateAsync(createRequest, userId, context.CancellationToken);
        return new ItemResponse { Item = ToMessage(item) };
    }

    public override async Task<ItemResponse> Update(UpdateItemGrpcRequest request, ServerCallContext context)
    {
        var updateRequest = new CreateItemRequest(request.Name, request.Description);
        var item = await _itemService.UpdateAsync(Guid.Parse(request.Id), updateRequest, context.CancellationToken);
        if (item is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Item {request.Id} not found"));

        return new ItemResponse { Item = ToMessage(item) };
    }

    public override async Task<DeleteItemResponse> Delete(DeleteItemRequest request, ServerCallContext context)
    {
        var deleted = await _itemService.DeleteAsync(Guid.Parse(request.Id), context.CancellationToken);
        return new DeleteItemResponse { Success = deleted };
    }

    private static ItemMessage ToMessage(ItemDto dto) => new()
    {
        Id = dto.Id.ToString(),
        Name = dto.Name,
        Description = dto.Description ?? "",
        Status = dto.Status,
        CreatedBy = dto.CreatedBy,
        CreatedAt = dto.CreatedAt.ToString("o"),
        UpdatedAt = dto.UpdatedAt?.ToString("o") ?? ""
    };
}
