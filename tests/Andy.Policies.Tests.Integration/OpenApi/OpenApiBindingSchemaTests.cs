// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text.Json;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.OpenApi;

/// <summary>
/// P3.8 (#26) OpenAPI acceptance for the binding surface (P3.3 + P3.4):
/// asserts the live Swashbuckle-generated document includes the four
/// binding paths with the documented status codes and that the request
/// + response component schemas are reachable. The committed
/// <c>docs/openapi/andy-policies-v1.yaml</c> snapshot is checked
/// separately by the <c>openapi-drift</c> CI job (P1.9, #79); this test
/// guards the runtime document against silent removal during refactors.
/// </summary>
public class OpenApiBindingSchemaTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    public OpenApiBindingSchemaTests(PoliciesApiFactory factory)
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
    [InlineData("/api/bindings", "post")]
    [InlineData("/api/bindings", "get")]
    [InlineData("/api/bindings/resolve", "get")]
    [InlineData("/api/bindings/{id}", "get")]
    [InlineData("/api/bindings/{id}", "delete")]
    [InlineData("/api/policies/{policyId}/versions/{versionId}/bindings", "get")]
    public async Task SwaggerJson_DocumentsBindingEndpoint(string path, string verb)
    {
        using var doc = await LoadSwaggerAsync();
        var paths = doc.RootElement.GetProperty("paths");

        paths.TryGetProperty(path, out var pathItem).Should().BeTrue(
            $"document must include {path}");
        pathItem.TryGetProperty(verb, out _).Should().BeTrue(
            $"{path} must define a {verb.ToUpper()} operation");
    }

    [Fact]
    public async Task SwaggerJson_CreateBindingPost_References_CreateBindingRequestSchema()
    {
        using var doc = await LoadSwaggerAsync();
        var post = doc.RootElement
            .GetProperty("paths").GetProperty("/api/bindings").GetProperty("post");

        var requestBody = post.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        requestBody.GetProperty("$ref").GetString().Should()
            .Be("#/components/schemas/CreateBindingRequest");

        // 201 returns a BindingDto; 409 on retired target.
        var responses = post.GetProperty("responses");
        responses.TryGetProperty("201", out _).Should().BeTrue();
        responses.TryGetProperty("400", out _).Should().BeTrue();
        responses.TryGetProperty("404", out _).Should().BeTrue();
        responses.TryGetProperty("409", out _).Should().BeTrue();
    }

    [Fact]
    public async Task SwaggerJson_ResolveGet_References_ResolveBindingsResponseSchema()
    {
        using var doc = await LoadSwaggerAsync();
        var get = doc.RootElement
            .GetProperty("paths").GetProperty("/api/bindings/resolve").GetProperty("get");

        var ok = get.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        ok.GetProperty("$ref").GetString().Should()
            .Be("#/components/schemas/ResolveBindingsResponse");
    }

    [Fact]
    public async Task SwaggerJson_ExposesBindingComponentSchemas()
    {
        using var doc = await LoadSwaggerAsync();
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        foreach (var name in new[]
                 {
                     "CreateBindingRequest",
                     "BindingDto",
                     "ResolveBindingsResponse",
                     "ResolvedBindingDto",
                     "BindingTargetType",
                     "BindStrength",
                 })
        {
            schemas.TryGetProperty(name, out _).Should().BeTrue($"schema {name} should be reachable");
        }
    }

    [Fact]
    public async Task SwaggerJson_BindingTargetType_ExposesAllFiveValues()
    {
        using var doc = await LoadSwaggerAsync();
        var schema = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("BindingTargetType");

        // JsonStringEnumConverter emits an "enum" with the string members.
        var values = schema.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        values.Should().BeEquivalentTo("Template", "Repo", "ScopeNode", "Tenant", "Org");
    }
}
