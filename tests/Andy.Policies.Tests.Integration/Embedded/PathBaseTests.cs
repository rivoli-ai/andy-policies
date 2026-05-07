// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Andy.Policies.Tests.Integration.Embedded;

/// <summary>
/// P10.2 (#35): with <c>AspNetCore:PathBase=/policies</c> set,
/// every API and SPA route mounts under <c>/policies/*</c>.
/// Conductor's embedded reverse proxy strips the prefix before
/// forwarding here; if pathbase wiring is wrong, every route
/// 404s either inbound (prefix not stripped) or outbound
/// (Swagger / OIDC redirects use the wrong base).
/// </summary>
public class PathBaseTests : IClassFixture<PoliciesApiFactory>, IDisposable
{
    private const string Prefix = "/policies";

    private readonly WebApplicationFactory<Program> _factory;

    public PathBaseTests(PoliciesApiFactory baseFactory)
    {
        // Inject the prefix via in-memory configuration on a derivative
        // factory rather than via the process-wide ASPNETCORE_PATHBASE
        // env var. Program.cs prefers Configuration["AspNetCore:PathBase"]
        // over the env var, so the scoped path is honoured exactly the
        // same way at host-build time. Why scoped: env vars are
        // process-global, and xUnit runs test classes in parallel — a
        // sibling test class building its host between this fixture's
        // constructor and Dispose would observe the prefix and fail
        // un-prefixed assertions, while a sibling Dispose nulling the
        // var mid-build of OUR host would unset the prefix before Swagger
        // wiring read it. The in-memory configuration override is
        // confined to this factory's IHost and removes the race entirely.
        _factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AspNetCore:PathBase"] = Prefix,
                });
            });
        });
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ApiRoute_UnderPrefix_Returns200()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync($"{Prefix}/api/policies");

        resp.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "with pathbase set, /policies/api/policies is the canonical " +
            "route — Conductor's proxy forwards inbound /policies/* and " +
            "UsePathBase strips the prefix before routing.");
    }

    [Fact]
    public async Task ApiRoute_WithoutPrefix_StillRoutes()
    {
        // Documents the actual UsePathBase behaviour: it STRIPS the
        // prefix when the request matches it, but does NOT enforce
        // prefix usage. Un-prefixed requests pass through and routing
        // matches them against the canonical un-prefixed routes.
        // Production prefix enforcement is the reverse proxy's job,
        // not the API's — Conductor's :9100 proxy only forwards
        // /policies/* to here, so consumers can never reach this
        // service through any other path.
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/policies");

        resp.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "UsePathBase is non-enforcing by design; the proxy gates the " +
            "prefix at the network edge");
    }

    [Fact]
    public async Task Health_UnderPrefix_Returns200()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync($"{Prefix}/health");

        resp.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the docker-compose.embedded.yml healthcheck targets " +
            "/policies/health; if this fails the container restarts in a " +
            "loop and Conductor never sees a healthy embedded service");
    }

    [Fact]
    public async Task Health_WithoutPrefix_StillRoutes()
    {
        // Same backwards-compat story as the API route test above —
        // un-prefixed health probes still resolve, the proxy enforces
        // prefixed-only access at the network edge.
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Swagger_UnderPrefix_ReturnsOpenApiDocument()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync($"{Prefix}/swagger/v1/swagger.json");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("openapi",
            "Swagger UI sits behind the same prefix as the API; consumers " +
            "browsing /policies/swagger expect a valid OpenAPI doc");
    }

}
