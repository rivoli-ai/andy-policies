// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using Andy.Policies.Application.Dtos;
using Xunit;

namespace Andy.Policies.Tests.Integration.Controllers;

// Items endpoints are template scaffolding — keeping the smoke tests around
// for now, but they share PoliciesApiFactory's TestAuthHandler + SQLite-backed
// AppDbContext rather than spinning up a separate factory. Bare
// WebApplicationFactory<Program> would hit production JWT auth and 401.
public class ItemsControllerTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    public ItemsControllerTests(PoliciesApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HealthCheck_ShouldReturnOk()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_ShouldReturnOk()
    {
        var response = await _client.GetAsync("/api/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_ShouldReturnCreated()
    {
        var request = new CreateItemRequest("Integration Test Item", "Created during test");
        var response = await _client.PostAsJsonAsync("/api/items", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var item = await response.Content.ReadFromJsonAsync<ItemDto>();
        Assert.NotNull(item);
        Assert.Equal("Integration Test Item", item!.Name);
    }

    [Fact]
    public async Task GetById_WhenNotFound_ShouldReturn404()
    {
        var response = await _client.GetAsync($"/api/items/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
