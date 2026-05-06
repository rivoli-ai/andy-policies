// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Application.Manifest;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Manifest;

/// <summary>
/// P10.3 (#38): pin the strongly-typed projection of
/// <c>config/registration.json</c>. If a future manifest edit drops a
/// scope, redirect URI, role, permission, resource type, or settings
/// key without updating the DTO, this test fails.
///
/// The on-disk JSON also covers the source-of-truth — the existing
/// <c>ManifestTests</c> (P7.1 #47) pins the rbac block; this set
/// extends the round-trip to the auth + settings blocks plus the DTO
/// shape.
/// </summary>
public class ManifestDocumentTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Andy.Policies.sln")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("test must run from inside the andy-policies repo");
        return Path.Combine(dir!.FullName, relativePath);
    }

    private static ManifestDocument LoadManifest()
    {
        var path = FindRepoFile("config/registration.json");
        var raw = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ManifestDocument>(raw, Json)
            ?? throw new InvalidOperationException("manifest deserialised to null");
    }

    [Fact]
    public void RegistrationJson_Deserialises_IntoManifestDocument()
    {
        var doc = LoadManifest();

        doc.Service.Name.Should().Be("andy-policies");
        doc.Auth.Audience.Should().Be("urn:andy-policies-api");
        doc.Rbac.ApplicationCode.Should().Be("andy-policies");
        doc.Settings.Definitions.Should().NotBeEmpty();
    }

    [Fact]
    public void Auth_ApiClient_RoundTripsAllFields()
    {
        var api = LoadManifest().Auth.ApiClient;

        api.ClientId.Should().Be("andy-policies-api");
        api.ClientType.Should().Be("confidential");
        api.ClientSecretEnvVar.Should().Be("ANDY_POLICIES_API_SECRET");
        api.GrantTypes.Should().Contain(new[]
        {
            "authorization_code", "refresh_token", "client_credentials"
        });
        api.Scopes.Should().Contain("scp:urn:andy-policies-api");
    }

    [Fact]
    public void Auth_WebClient_RoundTripsRedirectUris()
    {
        var web = LoadManifest().Auth.WebClient;

        web.ClientId.Should().Be("andy-policies-web");
        web.ClientType.Should().Be("public");
        web.RedirectUris.Should().NotBeEmpty();
        web.PostLogoutRedirectUris.Should().NotBeEmpty();
        web.RedirectUris.Should().Contain(uri => uri.Contains("/callback"));
    }

    [Fact]
    public void Rbac_RoundTripsRolesPermissionsAndResourceTypes()
    {
        var rbac = LoadManifest().Rbac;

        rbac.ResourceTypes.Should().NotBeEmpty();
        rbac.ResourceTypes.Select(rt => rt.Code).Should().Contain(new[]
        {
            "policy", "binding", "audit", "settings", "scope", "override", "bundle"
        });

        rbac.Roles.Select(r => r.Code).Should().Contain(new[]
        {
            "admin", "author", "approver", "risk", "viewer"
        });

        rbac.Permissions.Should().NotBeEmpty();
        rbac.Permissions.Select(p => p.Code).Should().Contain(
            "andy-policies:policy:read");
    }

    [Fact]
    public void Settings_RoundTripsAllDefinitions()
    {
        var settings = LoadManifest().Settings;

        settings.Definitions.Select(d => d.Key).Should().Contain(new[]
        {
            "andy.policies.bundleVersionPinning",
            "andy.policies.rationaleRequired",
            "andy.policies.auditRetentionDays",
            "andy.policies.experimentalOverridesEnabled",
        });

        var pinning = settings.Definitions
            .Single(d => d.Key == "andy.policies.bundleVersionPinning");
        pinning.DataType.Should().Be("Boolean");
        pinning.DefaultValue.Should().Be("true");
        pinning.AllowedScopes.Should().NotBeEmpty();
    }
}
