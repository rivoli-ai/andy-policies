// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Tools.GenerateRbacDocs;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Docs;

/// <summary>
/// Unit tests for <see cref="RbacCatalogGenerator"/> — pin the
/// markdown emission against in-memory JSON inputs so the docs-drift
/// CI check has a fast feedback loop on parser / renderer regressions
/// without spawning a process. P7.7 (#65).
/// </summary>
public class RbacDocsGeneratorTests
{
    private const string MinimalManifest = """
    {
      "rbac": {
        "applicationCode": "andy-policies",
        "resourceTypes": [
          { "code": "policy", "name": "Policy", "supportsInstances": true },
          { "code": "settings", "name": "Settings", "supportsInstances": false }
        ],
        "permissions": [
          { "code": "andy-policies:policy:read", "name": "Read", "resourceType": "policy" }
        ],
        "roles": [
          { "code": "admin", "name": "Administrator", "description": "Full access.", "permissionCodes": ["*"] },
          { "code": "viewer", "name": "Viewer", "description": "Read-only.", "permissionCodes": ["andy-policies:policy:read"] }
        ]
      }
    }
    """;

    private static string Generate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return RbacCatalogGenerator.Generate(doc.RootElement);
    }

    [Fact]
    public void Generate_EmitsHeader_AppCode_AndAllThreeSections()
    {
        var md = Generate(MinimalManifest);

        md.Should().Contain("# Permission catalog");
        md.Should().Contain(RbacCatalogGenerator.GeneratedHeader);
        md.Should().Contain("Application code: `andy-policies`");
        md.Should().Contain("## Resource types");
        md.Should().Contain("## Permissions");
        md.Should().Contain("## Roles");
    }

    [Fact]
    public void Generate_RendersResourceTypes_WithSupportsInstancesAsYesNo()
    {
        var md = Generate(MinimalManifest);

        md.Should().Contain("| `policy` | Policy | yes |");
        md.Should().Contain(
            "| `settings` | Settings | no |",
            "supportsInstances=false must render as 'no' so the published " +
            "doc tells consumers up front which resource types accept " +
            "instance ids on /api/check");
    }

    [Fact]
    public void Generate_RendersAdminWildcard_AsAllPermissions()
    {
        var md = Generate(MinimalManifest);

        md.Should().Contain(
            "`*` (all permissions)",
            "the admin role's permissionCodes=['*'] is the universal grant; " +
            "rendering the literal asterisk hides the semantics from auditors");
    }

    [Fact]
    public void Generate_RendersScopedRole_AsBacktickedCommaList()
    {
        var md = Generate(MinimalManifest);

        // The viewer row in the role table.
        md.Should().Contain("| `viewer` | Viewer | Read-only. | `andy-policies:policy:read` |");
    }

    [Fact]
    public void Generate_IsIdempotent_WhenInvokedTwiceOnSameInput()
    {
        var first = Generate(MinimalManifest);
        var second = Generate(MinimalManifest);

        second.Should().Be(
            first,
            "regenerating the catalog must be deterministic — otherwise the " +
            "drift CI check would flap on every PR even when the manifest " +
            "is unchanged");
    }

    [Fact]
    public void Generate_PreservesPermissionOrder_FromManifest()
    {
        var manifest = """
        {
          "rbac": {
            "applicationCode": "andy-policies",
            "resourceTypes": [{ "code": "policy", "name": "Policy", "supportsInstances": true }],
            "permissions": [
              { "code": "andy-policies:policy:zzz", "name": "Z", "resourceType": "policy" },
              { "code": "andy-policies:policy:aaa", "name": "A", "resourceType": "policy" }
            ],
            "roles": []
          }
        }
        """;

        var md = Generate(manifest);
        var zzzIdx = md.IndexOf("zzz", StringComparison.Ordinal);
        var aaaIdx = md.IndexOf("aaa", StringComparison.Ordinal);

        zzzIdx.Should().BeLessThan(
            aaaIdx,
            "permissions must render in manifest order — alphabetising would " +
            "break the curated grouping (read first, then mutating verbs) " +
            "and create churn whenever the manifest is edited");
    }

    [Fact]
    public void Generate_ThrowsClearError_WhenRbacSectionIsMissing()
    {
        var act = () => Generate("""{ "service": { "name": "andy-policies" } }""");

        act.Should().Throw<Exception>()
            .WithMessage("*missing*'rbac'*",
                "an empty / malformed manifest must fail loud — silent " +
                "fallback to an empty catalog would silently delete the " +
                "published doc on a manifest format change");
    }

    [Fact]
    public void Generate_ThrowsClearError_WhenPermissionsArrayIsMissing()
    {
        var manifest = """
        {
          "rbac": {
            "applicationCode": "andy-policies",
            "resourceTypes": [],
            "roles": []
          }
        }
        """;

        var act = () => Generate(manifest);

        act.Should().Throw<Exception>()
            .WithMessage("*permissions*missing*");
    }

    [Fact]
    public void Generate_EmitsCounts_MatchingArrayLengths()
    {
        var md = Generate(MinimalManifest);

        md.Should().Contain("- Resource types: 2");
        md.Should().Contain("- Permissions: 1");
        md.Should().Contain("- Roles: 2");
    }
}
