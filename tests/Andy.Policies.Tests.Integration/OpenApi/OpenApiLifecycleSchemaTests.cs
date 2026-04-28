// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text.Json;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.OpenApi;

/// <summary>
/// P2.8 (#18) OpenAPI acceptance: the lifecycle endpoints introduced in
/// P2.3 are present in the live Swashbuckle-generated document with the
/// 200/400/404/409 response shape, and the <c>LifecycleTransitionRequest</c>
/// component schema is reachable. The committed
/// <c>docs/openapi/andy-policies-v1.yaml</c> snapshot is checked separately
/// by the <c>openapi-drift</c> CI job (P1.9, #79); this test guards the
/// runtime document against silent removal during refactors.
/// </summary>
public class OpenApiLifecycleSchemaTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    public OpenApiLifecycleSchemaTests(PoliciesApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<JsonDocument> LoadSwaggerAsync()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Swashbuckle is registered in the Testing environment by Program.cs");
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    [Theory]
    [InlineData("/api/policies/{id}/versions/{versionId}/publish")]
    [InlineData("/api/policies/{id}/versions/{versionId}/winding-down")]
    [InlineData("/api/policies/{id}/versions/{versionId}/retire")]
    public async Task SwaggerJson_DocumentsLifecycleEndpoint(string path)
    {
        using var doc = await LoadSwaggerAsync();
        var paths = doc.RootElement.GetProperty("paths");

        paths.TryGetProperty(path, out var pathItem).Should().BeTrue(
            $"the document must include {path}");

        pathItem.TryGetProperty("post", out var op).Should().BeTrue(
            $"{path} must define a POST operation");

        var responses = op.GetProperty("responses");
        // Status codes from the controller's [ProducesResponseType] attributes.
        foreach (var status in new[] { "200", "400", "401", "404", "409" })
        {
            responses.TryGetProperty(status, out _).Should().BeTrue(
                $"{path} POST must declare a {status} response");
        }
    }

    [Fact]
    public async Task SwaggerJson_ExposesLifecycleTransitionRequestSchema()
    {
        using var doc = await LoadSwaggerAsync();
        var components = doc.RootElement.GetProperty("components").GetProperty("schemas");

        components.TryGetProperty("LifecycleTransitionRequest", out var schema).Should().BeTrue(
            "P2.3 introduced LifecycleTransitionRequest as the request body shape");

        var properties = schema.GetProperty("properties");
        properties.TryGetProperty("rationale", out _).Should().BeTrue(
            "the only field on the request is `rationale`");
    }

    [Fact]
    public async Task SwaggerJson_PublishOperation_ReferencesLifecycleTransitionRequest()
    {
        using var doc = await LoadSwaggerAsync();
        var publish = doc.RootElement
            .GetProperty("paths")
            .GetProperty("/api/policies/{id}/versions/{versionId}/publish")
            .GetProperty("post");

        var requestBody = publish.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        requestBody.GetProperty("$ref").GetString().Should()
            .Be("#/components/schemas/LifecycleTransitionRequest");

        // 200 returns a PolicyVersionDto.
        var ok = publish.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        ok.GetProperty("$ref").GetString().Should()
            .Be("#/components/schemas/PolicyVersionDto");
    }

    [Theory]
    [InlineData("/api/policies/{id}/versions/{versionId}/publish")]
    [InlineData("/api/policies/{id}/versions/{versionId}/winding-down")]
    [InlineData("/api/policies/{id}/versions/{versionId}/retire")]
    public async Task SwaggerJson_LifecycleEndpoints_AreTaggedAndCarryBearerSecurity(string path)
    {
        using var doc = await LoadSwaggerAsync();
        var op = doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty("post");

        op.GetProperty("tags").EnumerateArray().Select(t => t.GetString())
            .Should().Contain("PolicyVersionsLifecycle");

        // Bearer security requirement is added globally in Program.cs.
        // Each operation inherits it unless explicitly overridden.
        var hasBearer = doc.RootElement.GetProperty("security").EnumerateArray()
            .Any(req =>
            {
                JsonElement _;
                return req.TryGetProperty("Bearer", out _);
            });
        hasBearer.Should().BeTrue("Bearer security scheme is required globally");
    }
}
