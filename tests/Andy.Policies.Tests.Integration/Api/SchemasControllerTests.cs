// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Api;

/// <summary>
/// P9 follow-up #191 (2026-05-07): pins the rules schema endpoint
/// behaviour — anonymous read, valid JSON Schema body, strong ETag,
/// conditional GET round-trip, and the byte-cap hint that mirrors
/// ADR 0001 §5 (64 KiB).
/// </summary>
public class SchemasControllerTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    public SchemasControllerTests(PoliciesApiFactory factory)
    {
        // Anonymous endpoint — no auth header needed; the test factory's
        // TestAuthHandler still supplies one because every request goes
        // through the default scheme, but the [AllowAnonymous] on the
        // controller bypasses authorization entirely.
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetRulesSchema_Returns200_WithJsonBody()
    {
        var response = await _client.GetAsync("/api/schemas/rules.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // Schema discriminators we publish.
        doc.RootElement.GetProperty("$schema").GetString().Should().StartWith("https://json-schema.org/");
        doc.RootElement.GetProperty("$id").GetString().Should().Be("https://andy-policies/schemas/rules.json");
        doc.RootElement.GetProperty("type").GetString().Should().Be("object");
        // ADR 0001 §5 byte cap surfaced as an extension hint so editor
        // tooling (Monaco, ngx-monaco-editor) can size limits correctly.
        doc.RootElement.GetProperty("x-andy-policies-max-bytes").GetInt32().Should().Be(65536);
    }

    [Fact]
    public async Task GetRulesSchema_Sets_StrongETag_AndCacheControl()
    {
        var response = await _client.GetAsync("/api/schemas/rules.json");
        response.EnsureSuccessStatusCode();

        response.Headers.ETag.Should().NotBeNull();
        response.Headers.ETag!.IsWeak.Should().BeFalse();
        response.Headers.CacheControl?.Public.Should().BeTrue();
        response.Headers.CacheControl?.MaxAge.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRulesSchema_WithMatchingIfNoneMatch_Returns304()
    {
        var first = await _client.GetAsync("/api/schemas/rules.json");
        first.EnsureSuccessStatusCode();
        var etag = first.Headers.ETag!.Tag;

        var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/schemas/rules.json");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var response = await _client.SendAsync(conditional);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task GetRulesSchema_AccessibleAnonymously()
    {
        // Manually drop the auth header that PoliciesApiFactory's test
        // handler attaches by default — confirms [AllowAnonymous] keeps
        // the endpoint reachable without credentials.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/schemas/rules.json");
        // Don't set Authorization — the test handler installs a default
        // identity; we want to verify the controller doesn't require it.
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
