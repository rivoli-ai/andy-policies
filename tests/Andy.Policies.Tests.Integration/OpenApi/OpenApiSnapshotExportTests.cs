// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Tests.Integration.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Writers;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace Andy.Policies.Tests.Integration.OpenApi;

/// <summary>
/// Deterministic export entry point used by scripts/export-openapi.sh. Resolving
/// ISwaggerProvider from the already-supported test host avoids the CLI tool
/// waiting on production hosted services during design-time startup.
/// </summary>
public sealed class OpenApiSnapshotExportTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;

    public OpenApiSnapshotExportTests(PoliciesApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ExportYaml_WhenExplicitlyRequested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("UPDATE_OPENAPI"), "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<ISwaggerProvider>();
        var document = provider.GetSwagger("v1");
        var repoRoot = FindRepoRoot();
        var output = Path.Combine(repoRoot, "docs", "openapi", "andy-policies-v1.yaml");
        await using var stream = File.CreateText(output);
        var writer = new OpenApiYamlWriter(stream);
        document.SerializeAsV3(writer);
        await stream.FlushAsync();
    }

    private static string FindRepoRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null && !File.Exists(Path.Combine(cursor.FullName, "Andy.Policies.sln")))
        {
            cursor = cursor.Parent;
        }
        return cursor?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
