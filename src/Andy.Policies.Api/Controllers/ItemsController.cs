// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    public async Task<ActionResult<IEnumerable<ItemDto>>> GetAll(CancellationToken ct)
    {
        var items = await _itemService.GetAllAsync(ct);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ItemDto>> GetById(Guid id, CancellationToken ct)
    {
        var item = await _itemService.GetByIdAsync(id, ct);
        if (item is null) return NotFound();
        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<ItemDto>> Create([FromBody] CreateItemRequest request, CancellationToken ct)
    {
        var userId = User.Identity?.Name ?? "anonymous";
        var item = await _itemService.CreateAsync(request, userId, ct);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ItemDto>> Update(Guid id, [FromBody] CreateItemRequest request, CancellationToken ct)
    {
        var item = await _itemService.UpdateAsync(id, request, ct);
        if (item is null) return NotFound();
        return Ok(item);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await _itemService.DeleteAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}
