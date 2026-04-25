// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Tests.Integration.Controllers;
using Xunit;

namespace Andy.Policies.Tests.Integration.OpenApi;

/// <summary>
/// P1.9 (#79): the live <c>/swagger/v1/swagger.json</c> document is the
/// runtime contract REST clients consume. The committed
/// <c>docs/openapi/andy-policies-v1.yaml</c> is regenerated from this same
/// pipeline by <c>scripts/export-openapi.sh</c>; the CI drift check guards
/// the YAML, while these tests guard the *shape* the YAML is built from.
/// </summary>
public class SchemaShapeTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;

    public SchemaShapeTests(PoliciesApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SwaggerJson_IsReachable_AndValidJson()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var _ = JsonDocument.Parse(body); // throws on invalid JSON
    }

    [Fact]
    public async Task EveryOperation_HasOperationId()
    {
        using var doc = await LoadAsync();
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject())
            {
                if (!IsHttpMethod(op.Name))
                {
                    continue;
                }
                Assert.True(
                    op.Value.TryGetProperty("operationId", out var opId)
                    && opId.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(opId.GetString()),
                    $"Missing operationId for {op.Name.ToUpperInvariant()} {path.Name}");
            }
        }
    }

    [Fact]
    public async Task PolicyDto_IsExposed_WithExpectedProperties()
    {
        using var doc = await LoadAsync();
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("PolicyDto", out var dto), "PolicyDto schema missing");
        var props = dto.GetProperty("properties");
        foreach (var expected in new[] { "id", "name", "createdAt", "createdBySubjectId", "versionCount", "activeVersionId" })
        {
            Assert.True(props.TryGetProperty(expected, out _), $"PolicyDto missing property '{expected}'");
        }
    }

    [Fact]
    public async Task PolicyVersionDto_DimensionFields_AreEnumUnions()
    {
        using var doc = await LoadAsync();
        var version = doc.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("PolicyVersionDto")
            .GetProperty("properties");

        AssertEnum(version, "enforcement", new[] { "MUST", "SHOULD", "MAY" });
        AssertEnum(version, "severity", new[] { "info", "moderate", "critical" });
        AssertEnum(version, "state", new[] { "Draft", "Active", "WindingDown", "Retired" });
    }

    [Fact]
    public async Task SchemaComponents_DoNotExpose_RevisionConcurrencyToken()
    {
        using var doc = await LoadAsync();
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var schema in schemas.EnumerateObject())
        {
            if (!schema.Value.TryGetProperty("properties", out var props))
            {
                continue;
            }
            foreach (var prop in props.EnumerateObject())
            {
                Assert.False(
                    string.Equals(prop.Name, "revision", StringComparison.OrdinalIgnoreCase),
                    $"Schema '{schema.Name}' leaks the EF concurrency token 'revision'");
            }
        }
    }

    [Fact]
    public async Task BearerSecurityScheme_IsRegistered()
    {
        using var doc = await LoadAsync();
        var schemes = doc.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("Bearer", out var bearer), "Bearer security scheme missing");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task PolicyOperations_AreAllPresent()
    {
        using var doc = await LoadAsync();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject())
            {
                if (op.Value.TryGetProperty("operationId", out var opId) && opId.ValueKind == JsonValueKind.String)
                {
                    ids.Add(opId.GetString()!);
                }
            }
        }
        foreach (var expected in new[]
        {
            "Policies_List",
            "Policies_Create",
            "Policies_Get",
            "Policies_GetByName",
            "Policies_ListVersions",
            "Policies_GetActiveVersion",
            "Policies_GetVersion",
            "Policies_UpdateDraft",
            "Policies_Bump",
        })
        {
            Assert.Contains(expected, ids);
        }
    }

    private static void AssertEnum(JsonElement schemaProperties, string name, IReadOnlyList<string> expected)
    {
        Assert.True(schemaProperties.TryGetProperty(name, out var prop), $"Property '{name}' missing");
        Assert.True(prop.TryGetProperty("enum", out var enumProp), $"Property '{name}' has no enum");
        var actual = enumProp.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(expected.ToArray(), actual);
    }

    private static bool IsHttpMethod(string name) => name switch
    {
        "get" or "post" or "put" or "patch" or "delete" or "head" or "options" or "trace" => true,
        _ => false,
    };

    private async Task<JsonDocument> LoadAsync()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }
}
