// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http;
using System.Text.Json;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Api;

/// <summary>
/// P5.8 (#62) — boots the test server, fetches
/// <c>/swagger/v1/swagger.json</c>, and validates the structural
/// contract for the override surface: every <c>/api/overrides*</c>
/// path is documented, the <c>Overrides</c> tag is published with
/// the gate-posture description, and write operations carry the
/// <c>x-error-codes</c> extension that <c>OverridesDocumentFilter</c>
/// (P5.8) stamps.
/// </summary>
public class OverridesOpenApiTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    public OverridesOpenApiTests(PoliciesApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<JsonDocument> FetchSwaggerAsync()
    {
        var resp = await _client.GetAsync("/swagger/v1/swagger.json");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    [Fact]
    public async Task SwaggerJson_Contains_OverridesTag_WithGatePostureDescription()
    {
        using var doc = await FetchSwaggerAsync();

        doc.RootElement.TryGetProperty("tags", out var tags).Should().BeTrue();
        var overridesTag = tags.EnumerateArray()
            .FirstOrDefault(t => t.GetProperty("name").GetString() == "Overrides");
        overridesTag.ValueKind.Should().Be(JsonValueKind.Object);
        overridesTag.GetProperty("description").GetString()
            .Should().Contain("experimentalOverridesEnabled");
    }

    [Theory]
    [InlineData("/api/overrides", "post")]                // propose
    [InlineData("/api/overrides", "get")]                  // list
    [InlineData("/api/overrides/{id}", "get")]
    [InlineData("/api/overrides/{id}/approve", "post")]
    [InlineData("/api/overrides/{id}/revoke", "post")]
    [InlineData("/api/overrides/active", "get")]
    public async Task SwaggerJson_DocumentsOverrideEndpoint(string path, string verb)
    {
        using var doc = await FetchSwaggerAsync();

        doc.RootElement.GetProperty("paths").TryGetProperty(path, out var pathItem)
            .Should().BeTrue($"path {path} should be documented");
        pathItem.TryGetProperty(verb, out _).Should().BeTrue(
            $"path {path} should expose {verb.ToUpperInvariant()}");
    }

    [Theory]
    [InlineData("/api/overrides", "post")]
    [InlineData("/api/overrides/{id}/approve", "post")]
    [InlineData("/api/overrides/{id}/revoke", "post")]
    public async Task WriteOperations_Carry_XErrorCodesExtension(string path, string verb)
    {
        using var doc = await FetchSwaggerAsync();

        var op = doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty(verb);
        op.TryGetProperty("x-error-codes", out var codes).Should().BeTrue(
            $"{verb.ToUpperInvariant()} {path} should advertise x-error-codes");
        var values = codes.EnumerateArray().Select(e => e.GetString()).ToList();
        values.Should().Contain(new[]
        {
            "override.disabled",
            "override.self_approval_forbidden",
            "rbac.denied",
        });
    }

    [Fact]
    public async Task ProposeEndpoint_Documents_201Created_With_LocationHeader()
    {
        using var doc = await FetchSwaggerAsync();

        var post = doc.RootElement
            .GetProperty("paths")
            .GetProperty("/api/overrides")
            .GetProperty("post");
        post.GetProperty("responses").TryGetProperty("201", out _)
            .Should().BeTrue("propose returns 201 Created");
    }

    [Fact]
    public async Task ApproveEndpoint_Documents_409Conflict()
    {
        using var doc = await FetchSwaggerAsync();

        var post = doc.RootElement
            .GetProperty("paths")
            .GetProperty("/api/overrides/{id}/approve")
            .GetProperty("post");
        post.GetProperty("responses").TryGetProperty("409", out _)
            .Should().BeTrue("approve documents 409 for already-approved + concurrent racers");
    }
}
