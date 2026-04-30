// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text.Json;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.OpenApi;

/// <summary>
/// P4.7 (#36) OpenAPI acceptance for the scope surface (P4.5):
/// asserts the live Swashbuckle-generated document includes all six
/// <c>/api/scopes*</c> paths with the documented status codes and
/// that the request + response component schemas are reachable. The
/// committed <c>docs/openapi/andy-policies-v1.yaml</c> snapshot is
/// checked separately by the <c>openapi-drift</c> CI job (P1.9, #79);
/// this test guards the runtime document against silent removal
/// during refactors.
/// </summary>
public class OpenApiScopeSchemaTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    public OpenApiScopeSchemaTests(PoliciesApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<JsonDocument> LoadSwaggerAsync()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    [Theory]
    [InlineData("/api/scopes", "get")]
    [InlineData("/api/scopes", "post")]
    [InlineData("/api/scopes/tree", "get")]
    [InlineData("/api/scopes/{id}", "get")]
    [InlineData("/api/scopes/{id}", "delete")]
    [InlineData("/api/scopes/{id}/effective-policies", "get")]
    public async Task SwaggerJson_DocumentsScopeEndpoint(string path, string verb)
    {
        using var doc = await LoadSwaggerAsync();
        var paths = doc.RootElement.GetProperty("paths");

        paths.TryGetProperty(path, out var pathItem).Should().BeTrue(
            $"document must include {path}");
        pathItem.TryGetProperty(verb, out _).Should().BeTrue(
            $"{path} must define a {verb.ToUpper()} operation");
    }

    [Fact]
    public async Task SwaggerJson_CreateScopePost_References_CreateScopeNodeRequestSchema()
    {
        using var doc = await LoadSwaggerAsync();
        var post = doc.RootElement
            .GetProperty("paths").GetProperty("/api/scopes").GetProperty("post");

        var requestBody = post.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        requestBody.GetProperty("$ref").GetString().Should()
            .Be("#/components/schemas/CreateScopeNodeRequest");

        var responses = post.GetProperty("responses");
        responses.TryGetProperty("201", out _).Should().BeTrue();
        responses.TryGetProperty("400", out _).Should().BeTrue();
        responses.TryGetProperty("404", out _).Should().BeTrue();
        responses.TryGetProperty("409", out _).Should().BeTrue();
    }

    [Fact]
    public async Task SwaggerJson_EffectivePoliciesGet_References_EffectivePolicySetDtoSchema()
    {
        using var doc = await LoadSwaggerAsync();
        var get = doc.RootElement
            .GetProperty("paths")
            .GetProperty("/api/scopes/{id}/effective-policies")
            .GetProperty("get");

        var ok = get.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        ok.GetProperty("$ref").GetString().Should()
            .Be("#/components/schemas/EffectivePolicySetDto");
    }

    [Fact]
    public async Task SwaggerJson_TreeGet_References_ScopeTreeDtoArrayOrSchema()
    {
        using var doc = await LoadSwaggerAsync();
        var get = doc.RootElement
            .GetProperty("paths").GetProperty("/api/scopes/tree").GetProperty("get");

        var ok = get.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        // The action returns IReadOnlyList<ScopeTreeDto>, which Swashbuckle
        // typically emits as an array. Either an array of refs or a direct
        // ref is acceptable; both indicate the schema is reachable.
        var hasItems = ok.TryGetProperty("items", out var items);
        if (hasItems)
        {
            items.GetProperty("$ref").GetString().Should()
                .Be("#/components/schemas/ScopeTreeDto");
        }
        else
        {
            ok.GetProperty("$ref").GetString().Should()
                .Be("#/components/schemas/ScopeTreeDto");
        }
    }

    [Fact]
    public async Task SwaggerJson_ExposesScopeComponentSchemas()
    {
        using var doc = await LoadSwaggerAsync();
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        foreach (var name in new[]
                 {
                     "CreateScopeNodeRequest",
                     "ScopeNodeDto",
                     "ScopeTreeDto",
                     "EffectivePolicySetDto",
                     "EffectivePolicyDto",
                     "ScopeType",
                 })
        {
            schemas.TryGetProperty(name, out _).Should().BeTrue($"schema {name} should be reachable");
        }
    }

    [Fact]
    public async Task SwaggerJson_ScopeType_IsEmittedAsStringEnum_WithAllSixValues()
    {
        using var doc = await LoadSwaggerAsync();
        var schema = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("ScopeType");

        var values = schema.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        values.Should().BeEquivalentTo("Org", "Tenant", "Team", "Repo", "Template", "Run");
    }
}
