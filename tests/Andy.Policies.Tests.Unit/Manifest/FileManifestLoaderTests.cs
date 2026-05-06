// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Infrastructure.Manifest;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Manifest;

/// <summary>
/// P10.3 (#38): boundary tests for <see cref="FileManifestLoader"/>.
/// Anchored on a temp directory rather than the repo so the cases
/// don't depend on the live <c>config/registration.json</c>.
/// </summary>
public class FileManifestLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public FileManifestLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"andy-policies-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ThrowsFileNotFound()
    {
        var path = Path.Combine(_tempDir, "registration.json");
        var loader = MakeLoader(path);

        var act = async () => await loader.LoadAsync(CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_ThrowsJsonException()
    {
        var path = Path.Combine(_tempDir, "registration.json");
        await File.WriteAllTextAsync(path, "{ this is not json");

        var loader = MakeLoader(path);

        var act = async () => await loader.LoadAsync(CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task LoadAsync_ValidJson_DeserialisesIntoDocument()
    {
        var path = Path.Combine(_tempDir, "registration.json");
        await File.WriteAllTextAsync(path, MinimalManifestJson());

        var loader = MakeLoader(path);

        var doc = await loader.LoadAsync(CancellationToken.None);

        doc.Service.Name.Should().Be("andy-policies");
        doc.Auth.Audience.Should().Be("urn:test");
        doc.Rbac.ApplicationCode.Should().Be("andy-policies");
        doc.Settings.Definitions.Should().HaveCount(1);
    }

    // Uses the internal test-only constructor on FileManifestLoader.
    // Tests.Unit has InternalsVisibleTo from Andy.Policies.Infrastructure.
    private static FileManifestLoader MakeLoader(string path) => new(path);

    private static string MinimalManifestJson() => """
        {
          "service": {
            "name": "andy-policies",
            "displayName": "Andy Policies",
            "description": "test"
          },
          "auth": {
            "audience": "urn:test",
            "apiClient": {
              "clientId": "andy-policies-api",
              "clientType": "confidential",
              "clientSecretEnvVar": "X",
              "displayName": "API",
              "description": "API",
              "grantTypes": ["client_credentials"],
              "scopes": []
            },
            "webClient": {
              "clientId": "andy-policies-web",
              "clientType": "public",
              "displayName": "Web",
              "description": "Web",
              "grantTypes": ["authorization_code"],
              "scopes": [],
              "redirectUris": [],
              "postLogoutRedirectUris": []
            }
          },
          "rbac": {
            "applicationCode": "andy-policies",
            "applicationName": "Andy Policies",
            "description": "test",
            "resourceTypes": [],
            "permissions": [],
            "roles": []
          },
          "settings": {
            "definitions": [
              {
                "key": "andy.policies.test",
                "displayName": "Test",
                "description": "test",
                "category": "Test",
                "dataType": "Boolean",
                "defaultValue": "false",
                "allowedScopes": ["Application"]
              }
            ]
          }
        }
        """;
}
