// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Tools.GenerateRbacDocs;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Docs;

/// <summary>
/// P7.7 (#65) — pins the relationship between
/// <c>config/registration.json</c> and the committed
/// <c>docs/reference/permission-catalog.md</c>. The CI workflow runs
/// <c>tools/GenerateRbacDocs --check</c> to gate the same invariant;
/// this xUnit form lets contributors hit the same failure locally
/// during <c>dotnet test</c> instead of waiting for CI feedback.
/// </summary>
public class DocsDriftTests
{
    [Fact]
    public void GeneratedCatalog_MatchesCommittedFile_ByteForByte()
    {
        var repoRoot = ResolveRepoRoot();
        var manifestPath = Path.Combine(repoRoot, "config", "registration.json");
        var catalogPath = Path.Combine(repoRoot, "docs", "reference", "permission-catalog.md");

        File.Exists(manifestPath).Should().BeTrue($"missing manifest at {manifestPath}");
        File.Exists(catalogPath).Should().BeTrue($"missing catalog at {catalogPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var generated = RbacCatalogGenerator.Generate(doc.RootElement);
        var committed = File.ReadAllText(catalogPath);

        Normalize(generated).Should().Be(
            Normalize(committed),
            "docs/reference/permission-catalog.md is out of sync with " +
            "config/registration.json. Regenerate via " +
            "`dotnet run --project tools/GenerateRbacDocs` and commit the result.");
    }

    [Fact]
    public void GeneratedCatalog_ContainsEveryManifestPermission()
    {
        var repoRoot = ResolveRepoRoot();
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repoRoot, "config", "registration.json")));
        var generated = RbacCatalogGenerator.Generate(doc.RootElement);

        // Cross-check: a regression that emits an empty permissions
        // table on a non-empty manifest would still byte-match the
        // committed file if the file was also regenerated wrong; this
        // test pins the lower bound independently.
        var permissionsArray = doc.RootElement
            .GetProperty("rbac")
            .GetProperty("permissions");
        permissionsArray.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var perm in permissionsArray.EnumerateArray())
        {
            var code = perm.GetProperty("code").GetString()!;
            generated.Should().Contain(
                code,
                $"every manifest permission code must surface in the catalog page; '{code}' is missing");
        }
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Andy.Policies.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate Andy.Policies.sln walking up from {AppContext.BaseDirectory}.");
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
}
