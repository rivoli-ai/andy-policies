// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Authorization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Andy.Policies.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ItemsController : ControllerBase
{
    private readonly IItemService _itemService;

    public ItemsController(IItemService itemService)
    {
        _itemService = itemService;
    }

    [HttpGet]
    [Authorize(Policy = "andy-policies:policy:read")]
    public async Task<ActionResult<IEnumerable<ItemDto>>> GetAll(CancellationToken ct)
    {
        var items = await _itemService.GetAllAsync(ct);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "andy-policies:policy:read")]
    public async Task<ActionResult<ItemDto>> GetById(Guid id, CancellationToken ct)
    {
        var item = await _itemService.GetByIdAsync(id, ct);
        if (item is null) return NotFound();
        return Ok(item);
    }

    [HttpPost]
    [Authorize(Policy = "andy-policies:policy:author")]
    public async Task<ActionResult<ItemDto>> Create([FromBody] CreateItemRequest request, CancellationToken ct)
    {
        var userId = ActorSubjectResolver.Require(User);
        var item = await _itemService.CreateAsync(request, userId, ct);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "andy-policies:policy:author")]
    public async Task<ActionResult<ItemDto>> Update(Guid id, [FromBody] CreateItemRequest request, CancellationToken ct)
    {
        var item = await _itemService.UpdateAsync(
            id, request, ActorSubjectResolver.Require(User), ct);
        if (item is null) return NotFound();
        return Ok(item);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "andy-policies:policy:author")]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeleteItemMutationRequest? request,
        CancellationToken ct)
    {
        var deleted = await _itemService.DeleteAsync(
            id, ActorSubjectResolver.Require(User), request?.Rationale, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    public sealed record DeleteItemMutationRequest(string? Rationale);
}
