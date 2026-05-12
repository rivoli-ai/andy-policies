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
    private const string AuthBaseUrl = "http://localhost:7002";
    private const string AuthTokenUrl = AuthBaseUrl + "/connect/token";
    private const string PoliciesBaseUrl = "http://localhost:7113";
    private const string RbacBaseUrl = "http://localhost:7004";
    private const string SettingsBaseUrl = "http://localhost:7301";
    private const string ClientId = "andy-policies-api";
    private const string ClientSecret = "e2e-test-secret-not-for-production";
    private const string Audience = "urn:andy-policies-api";

    // andy-policies-web — public OAuth client declared in
    // config/registration.json; allowed redirect URIs include the one below.
    private const string WebClientId = "andy-policies-web";
    private const string WebRedirectUri = "http://localhost:9100/policies/callback";

    // Test user is auto-seeded by andy-auth in non-Production environments
    // (DbSeeder.cs:1093). Id is pinned to a well-known constant by #56/#57
    // so andy-rbac (#52/#53) can pre-bind roles via manifest.testUserRole
    // before the user ever authenticates.
    private const string TestUserEmail = "test@andy.local";
    private const string TestUserPassword = "Test123!";
    private const string TestUserWellKnownId = "00000000-0000-0000-0000-000000000001";

    // Companion no-permissions user seeded by andy-auth's DbSeeder (#95 upstream).
    // Deliberately not bound to any role in andy-rbac, so any [Authorize(Policy=...)]
    // endpoint should return 403 for this subject (per PermissionEvaluator's
    // "Subject not found" fail-closed branch).
    private const string ViewerUserEmail = "viewer@andy.local";
    private const string ViewerUserPassword = "Test123!";
    private const string ViewerUserWellKnownId = "00000000-0000-0000-0000-000000000002";

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

    [Fact]
    [Trait("Category", "E2E")]
    public async Task AndyRbac_HealthAndManifestSeeding_Verified()
    {
        if (!E2EEnabled) return;

        // andy-rbac is up.
        var health = await _http.GetAsync($"{RbacBaseUrl}/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        // andy-rbac consumed /monorepo/andy-policies/config/registration.json
        // at startup and seeded the andy-policies application + roles +
        // resource types. /api/applications is [AllowAnonymous] in andy-rbac,
        // so we can introspect without a JWT.
        var apps = await _http.GetAsync($"{RbacBaseUrl}/api/applications");
        Assert.Equal(HttpStatusCode.OK, apps.StatusCode);

        using var doc = JsonDocument.Parse(await apps.Content.ReadAsStringAsync());
        var codes = new List<string>();
        foreach (var app in doc.RootElement.EnumerateArray())
        {
            if (app.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            {
                codes.Add(code.GetString()!);
            }
        }
        Assert.Contains("andy-policies", codes);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task AndySettings_Health_Verified()
    {
        if (!E2EEnabled) return;

        // andy-settings is up. Deep manifest verification (/api/definitions
        // → 4 declared settings present) requires a JWT for
        // urn:andy-settings-api and is deferred to #108 (foundational
        // settings client wiring), where we'll exercise that path naturally.
        var health = await _http.GetAsync($"{SettingsBaseUrl}/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task UserAuthCode_TokenAccepted_ByPoliciesApi()
    {
        if (!E2EEnabled) return;

        // 1. Acquire JWT for test@andy.local via authorization_code + PKCE.
        //    Uses andy-auth's TestLogin endpoint (Development-only, ignores
        //    anti-forgery) to establish the session cookie, then walks
        //    /connect/authorize → /connect/token like a real public client.
        // We only request the API audience scope — the andy-policies-web client
        // is seeded with raw "email"/"profile"/"roles" permission strings (no
        // scp: prefix, an upstream andy-auth quirk), so requesting those would
        // be rejected. The audience scope is the one we actually need on the
        // resulting access token anyway.
        using var flow = new AuthorizationCodeFlow(
            authBaseUrl: AuthBaseUrl,
            clientId: WebClientId,
            redirectUri: WebRedirectUri,
            scope: Audience);

        var token = await flow.AcquireUserAccessTokenAsync(TestUserEmail, TestUserPassword);
        Assert.False(string.IsNullOrEmpty(token), "andy-auth returned an empty access_token for test user");

        // 2. Use the user JWT against andy-policies. Today this returns 200
        //    for any authenticated user — RBAC enforcement lands in Epic P7
        //    (#51 IRbacChecker + #57 authorization handlers). When P7 ships,
        //    add a companion test that asserts a non-admin user gets 403.
        var req = new HttpRequestMessage(HttpMethod.Get, $"{PoliciesBaseUrl}/api/policies");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task AndyRbac_TestUser_HasAdminRoleBindingForPolicies()
    {
        if (!E2EEnabled) return;

        // Verifies the cross-repo coordination from rivoli-ai/andy-auth#57
        // + rivoli-ai/andy-rbac#53: andy-rbac's DataSeeder consumes
        // testUserRole from andy-policies/config/registration.json and binds
        // the well-known test subject to the admin role on andy-policies.
        // /api/subjects/by-external is anonymous in andy-rbac today, so we
        // can introspect without a JWT.
        var url = $"{RbacBaseUrl}/api/subjects/by-external/andy-auth/{TestUserWellKnownId}";
        var res = await _http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var roles = doc.RootElement.GetProperty("roles");

        var hasAdminOnPolicies = false;
        foreach (var role in roles.EnumerateArray())
        {
            var roleCode = role.GetProperty("roleCode").GetString();
            var appCode = role.TryGetProperty("applicationCode", out var ac) ? ac.GetString() : null;
            if (roleCode == "admin" && appCode == "andy-policies")
            {
                hasAdminOnPolicies = true;
                break;
            }
        }

        Assert.True(hasAdminOnPolicies,
            $"Expected test user ({TestUserEmail}) to have admin role on andy-policies after manifest seeding. " +
            $"Roles found: {roles}");
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task UserWithAdminRole_CanCreatePolicy_Returns201()
    {
        if (!E2EEnabled) return;

        // P7 acceptance criterion (#109): a JWT for a user *with* the
        // andy-policies admin role can mutate the catalog. test@andy.local
        // gets admin bound via manifest testUserRole (see
        // AndyRbac_TestUser_HasAdminRoleBindingForPolicies).
        using var flow = new AuthorizationCodeFlow(
            authBaseUrl: AuthBaseUrl,
            clientId: WebClientId,
            redirectUri: WebRedirectUri,
            scope: Audience);

        var token = await flow.AcquireUserAccessTokenAsync(TestUserEmail, TestUserPassword);
        Assert.False(string.IsNullOrEmpty(token), "andy-auth returned an empty access_token for admin user");

        var slug = $"e2e-admin-{Guid.NewGuid():N}".Substring(0, 18);
        var create = new CreatePolicyRequest(
            Name: slug,
            Description: "Created by UserWithAdminRole_CanCreatePolicy_Returns201",
            Summary: "rbac-admin-smoke",
            Enforcement: "Must",
            Severity: "Critical",
            Scopes: new[] { "prod" },
            RulesJson: "{}");

        var req = new HttpRequestMessage(HttpMethod.Post, $"{PoliciesBaseUrl}/api/policies")
        {
            Content = JsonContent.Create(create),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task UserWithoutAdminRole_CannotCreatePolicy_Returns403()
    {
        if (!E2EEnabled) return;

        // P7 acceptance criterion (#109): a JWT for an authenticated user
        // *without* the andy-policies admin role is rejected at the RBAC
        // gate. viewer@andy.local is seeded by andy-auth (#95) with no
        // role binding in andy-rbac; PermissionEvaluator returns
        // "Subject not found" → RbacAuthorizationHandler fails the policy
        // → ASP.NET returns 403.
        using var flow = new AuthorizationCodeFlow(
            authBaseUrl: AuthBaseUrl,
            clientId: WebClientId,
            redirectUri: WebRedirectUri,
            scope: Audience);

        var token = await flow.AcquireUserAccessTokenAsync(ViewerUserEmail, ViewerUserPassword);
        Assert.False(string.IsNullOrEmpty(token), "andy-auth returned an empty access_token for viewer user");

        var slug = $"e2e-viewer-{Guid.NewGuid():N}".Substring(0, 18);
        var create = new CreatePolicyRequest(
            Name: slug,
            Description: "Should be rejected by RBAC",
            Summary: "rbac-deny-smoke",
            Enforcement: "Must",
            Severity: "Critical",
            Scopes: new[] { "prod" },
            RulesJson: "{}");

        var req = new HttpRequestMessage(HttpMethod.Post, $"{PoliciesBaseUrl}/api/policies")
        {
            Content = JsonContent.Create(create),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
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
