// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using Andy.Policies.Application.Manifest;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// #216 — `GET /api/auth/permissions` returns the current subject's
/// allow-set, sourced from the registration manifest's permission
/// catalog and resolved through <see cref="Andy.Policies.Application.Interfaces.IRbacChecker"/>.
/// PoliciesApiFactory wires an allow-all rbac stub, so under that
/// stub the response should mirror the full catalog from the manifest.
/// </summary>
public class AuthControllerTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    public AuthControllerTests(PoliciesApiFactory factory)
    {
        // The production FileManifestLoader resolves the manifest off
        // the host's ContentRootPath which is the test bin dir under
        // WebApplicationFactory — the manifest isn't shipped there.
        // Stub the loader with a fixed catalog containing the codes
        // the test asserts on. This is the same pattern as the rbac /
        // rationale stubs the base factory installs.
        var derivative = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IManifestLoader))
                    .ToList();
                foreach (var d in existing) services.Remove(d);
                services.AddSingleton<IManifestLoader>(new StubManifestLoader());
            });
        });
        _client = derivative.CreateClient();
    }

    [Fact]
    public async Task Permissions_AllowAllStub_ReturnsFullCatalog()
    {
        var resp = await _client.GetAsync("/api/auth/permissions");
        resp.EnsureSuccessStatusCode();

        var allowed = await resp.Content.ReadFromJsonAsync<List<string>>();
        allowed.Should().NotBeNull();
        // Under the allow-all rbac stub the response is the full
        // catalog. Spot-check the codes the SPA gates publish-workflow
        // buttons against — a missing one would mean the manifest fell
        // out of sync with the new propose/reject endpoints.
        allowed!.Should().Contain(new[]
        {
            "andy-policies:policy:publish",
            "andy-policies:policy:propose",
            "andy-policies:policy:reject",
        });
    }

    private sealed class StubManifestLoader : IManifestLoader
    {
        public Task<ManifestDocument> LoadAsync(CancellationToken ct)
            => Task.FromResult(new ManifestDocument(
                Service: new ManifestService("andy-policies", "Policies", "test"),
                Auth: new ManifestAuth(
                    Audience: "urn:test",
                    ApiClient: new ManifestApiClient("c", "ApiClient", null, "c", "c",
                        Array.Empty<string>(), Array.Empty<string>()),
                    WebClient: new ManifestWebClient("c", "WebClient", "c", "c",
                        Array.Empty<string>(), Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>())),
                Rbac: new ManifestRbac(
                    ApplicationCode: "andy-policies",
                    ApplicationName: "Andy Policies",
                    Description: "test",
                    ResourceTypes: Array.Empty<ManifestResourceType>(),
                    Permissions: new[]
                    {
                        new ManifestPermission("andy-policies:policy:read", "r", "policy"),
                        new ManifestPermission("andy-policies:policy:publish", "p", "policy"),
                        new ManifestPermission("andy-policies:policy:propose", "p", "policy"),
                        new ManifestPermission("andy-policies:policy:reject", "rj", "policy"),
                    },
                    Roles: Array.Empty<ManifestRole>(),
                    TestUserRole: null),
                Settings: new ManifestSettings(Array.Empty<ManifestSettingDefinition>())));
    }
}
