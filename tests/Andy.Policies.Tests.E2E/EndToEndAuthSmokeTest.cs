// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Xunit;

namespace Andy.Policies.Tests.E2E;

/// <summary>
/// End-to-end auth smoke test (issue rivoli-ai/andy-policies#105).
///
/// Requires the live stack from <c>docker-compose.e2e.yml</c> to be running:
/// <code>
/// docker compose -f docker-compose.e2e.yml up -d --build
/// E2E_ENABLED=1 dotnet test tests/Andy.Policies.Tests.E2E
/// </code>
///
/// Skipped by default (env var <c>E2E_ENABLED</c> must be set) so the standard
/// <c>dotnet test</c> run on dev machines / CI without Docker doesn't require it.
///
/// What this test proves:
/// <list type="bullet">
///   <item>The OAuth client (<c>andy-policies-api</c>) declared in
///   <c>config/registration.json</c> is consumable by a running andy-auth.</item>
///   <item>andy-auth issues a JWT for the configured audience
///   (<c>urn:andy-policies-api</c>).</item>
///   <item>andy-policies validates the JWT (issuer, signature, audience) end-to-end.</item>
///   <item>The full REST surface accepts the resulting Bearer token.</item>
/// </list>
///
/// What this test does NOT prove (deferred):
/// <list type="bullet">
///   <item>User-claim flow — uses client_credentials so the token represents
///   a service principal, not <c>test@andy.local</c>. When P7 (#57) lands,
///   add a second test that exercises authorization_code flow.</item>
///   <item>andy-rbac / andy-settings registration — those services aren't
///   in the e2e compose v1.</item>
/// </list>
/// </summary>
public sealed class EndToEndAuthSmokeTest : IAsyncLifetime
{
    private const string AuthTokenUrl = "http://localhost:7002/connect/token";
    private const string PoliciesBaseUrl = "http://localhost:7113";
    private const string ClientId = "andy-policies-api";
    private const string ClientSecret = "e2e-test-secret-not-for-production";
    private const string Audience = "urn:andy-policies-api";

    private readonly HttpClient _http = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _http.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Skip unless E2E_ENABLED=1.</summary>
    private static bool E2EEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("E2E_ENABLED"), "1", StringComparison.Ordinal);

    [Fact]
    [Trait("Category", "E2E")]
    public async Task ClientCredentials_TokenAccepted_ByPoliciesApi()
    {
        if (!E2EEnabled)
        {
            // xUnit's Skip API would be cleaner but requires Skippable;
            // returning quietly avoids a noisy failure on dev machines.
            return;
        }

        // 1. Acquire a JWT from andy-auth via client_credentials.
        var token = await AcquireAccessTokenAsync();
        Assert.False(string.IsNullOrEmpty(token), "andy-auth returned an empty access_token");

        // 2. Create a policy through the REST surface using the Bearer token.
        var slug = $"e2e-{Guid.NewGuid():N}".Substring(0, 16);
        var create = new CreatePolicyRequest(
            Name: slug,
            Description: "Created by EndToEndAuthSmokeTest",
            Summary: "smoke",
            Enforcement: "Must",
            Severity: "Critical",
            Scopes: new[] { "prod" },
            RulesJson: "{}");

        var createReq = new HttpRequestMessage(HttpMethod.Post, $"{PoliciesBaseUrl}/api/policies")
        {
            Content = JsonContent.Create(create),
        };
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var createRes = await _http.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        var version = await createRes.Content.ReadFromJsonAsync<PolicyVersionDto>();
        Assert.NotNull(version);
        Assert.Equal(1, version!.Version);
        Assert.Equal("MUST", version.Enforcement);

        // 3. Read it back through a separate authenticated request — proves the
        // same token is accepted on subsequent calls (caching, key rotation).
        var listReq = new HttpRequestMessage(HttpMethod.Get,
            $"{PoliciesBaseUrl}/api/policies/by-name/{slug}");
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var listRes = await _http.SendAsync(listReq);
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);

        var policy = await listRes.Content.ReadFromJsonAsync<PolicyDto>();
        Assert.Equal(slug, policy!.Name);
        Assert.Equal(1, policy.VersionCount);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task UnauthenticatedRequest_Returns401()
    {
        if (!E2EEnabled) return;

        // No Authorization header — andy-policies must reject (proves the bypass
        // really is gone; #103).
        var res = await _http.GetAsync($"{PoliciesBaseUrl}/api/policies");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    private async Task<string> AcquireAccessTokenAsync()
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", ClientId),
            new KeyValuePair<string, string>("client_secret", ClientSecret),
            new KeyValuePair<string, string>("scope", Audience),
        });

        var res = await _http.PostAsync(AuthTokenUrl, form);
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode,
            $"Token endpoint returned {(int)res.StatusCode} {res.ReasonPhrase}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
    }
}
