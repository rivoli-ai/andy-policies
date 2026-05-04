// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Registration;

/// <summary>
/// P7.1 (#47) — pins the RBAC permission catalog declared in
/// <c>config/registration.json</c> so any future epic that adds a
/// catalog mutation without extending the manifest fails here.
///
/// Asserts:
///   1. Completeness — every action in the P1–P6/P8 allow-list maps
///      to exactly one permission code.
///   2. Referential integrity — every <c>role.permissionCodes[]</c>
///      entry references a declared permission (or the literal
///      <c>"*"</c>); every <c>permissions[].resourceType</c>
///      references a declared <c>resourceTypes[].code</c>.
///   3. Role-shape invariants — admin holds <c>"*"</c>; viewer only
///      holds <c>:read</c> codes; author does not hold
///      <c>policy:publish</c>; approver does not hold
///      <c>policy:author</c>.
///   4. Seed parity — <c>config/rbac-seed.json</c> is byte-equal to
///      the <c>rbac</c> sub-tree of <c>config/registration.json</c>
///      (re-serialised from the same parsed object so JSON formatting
///      differences don't matter, only semantic content does).
/// </summary>
public class ManifestTests
{
    private const string AdminWildcard = "*";

    private static readonly string[] ExpectedActionAllowlist =
    {
        // P1 — drafting
        "andy-policies:policy:read",
        "andy-policies:policy:author",
        // P2 — lifecycle transitions
        "andy-policies:policy:publish",
        "andy-policies:policy:transition",
        // P3 — bindings
        "andy-policies:binding:read",
        "andy-policies:binding:manage",
        // P4 — scope tree
        "andy-policies:scope:read",
        "andy-policies:scope:manage",
        // P5 — overrides
        "andy-policies:override:read",
        "andy-policies:override:propose",
        "andy-policies:override:approve",
        "andy-policies:override:revoke",
        // P6 — audit
        "andy-policies:audit:read",
        "andy-policies:audit:export",
        "andy-policies:audit:verify",
        // P8 — bundles
        "andy-policies:bundle:read",
        "andy-policies:bundle:create",
        "andy-policies:bundle:delete",
    };

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Andy.Policies.sln")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("test must run from inside the andy-policies repo");
        var path = Path.Combine(dir!.FullName, relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} should exist at the repo root");
        return path;
    }

    private static JsonElement LoadRbacBlock()
    {
        var path = FindRepoFile("config/registration.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rbac = doc.RootElement.GetProperty("rbac");
        return rbac.Clone();
    }

    private static JsonElement LoadRbacSeed()
    {
        var path = FindRepoFile("config/rbac-seed.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void RegistrationJson_ParsesAndExposesRbacBlock()
    {
        var rbac = LoadRbacBlock();

        rbac.GetProperty("applicationCode").GetString().Should().Be("andy-policies");
        rbac.GetProperty("permissions").ValueKind.Should().Be(JsonValueKind.Array);
        rbac.GetProperty("roles").ValueKind.Should().Be(JsonValueKind.Array);
        rbac.GetProperty("resourceTypes").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public void Permissions_CoverEveryActionInAllowlist()
    {
        var rbac = LoadRbacBlock();
        var declared = rbac.GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetProperty("code").GetString())
            .ToHashSet();

        foreach (var action in ExpectedActionAllowlist)
        {
            declared.Should().Contain(action,
                $"action '{action}' must map to a declared permission code");
        }
    }

    [Fact]
    public void Permissions_HaveNoDuplicateCodes()
    {
        var rbac = LoadRbacBlock();
        var codes = rbac.GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetProperty("code").GetString()!)
            .ToList();

        codes.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Permissions_ResourceType_ReferencesDeclaredType()
    {
        var rbac = LoadRbacBlock();
        var resourceTypes = rbac.GetProperty("resourceTypes").EnumerateArray()
            .Select(rt => rt.GetProperty("code").GetString()!)
            .ToHashSet();

        foreach (var perm in rbac.GetProperty("permissions").EnumerateArray())
        {
            var rt = perm.GetProperty("resourceType").GetString();
            resourceTypes.Should().Contain(rt,
                $"permission '{perm.GetProperty("code").GetString()}' references undeclared resourceType '{rt}'");
        }
    }

    [Fact]
    public void Roles_PermissionCodes_ReferenceDeclaredPermissions()
    {
        var rbac = LoadRbacBlock();
        var declared = rbac.GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetProperty("code").GetString()!)
            .ToHashSet();

        foreach (var role in rbac.GetProperty("roles").EnumerateArray())
        {
            var roleCode = role.GetProperty("code").GetString();
            var perms = role.GetProperty("permissionCodes").EnumerateArray()
                .Select(p => p.GetString()!)
                .ToList();

            foreach (var perm in perms)
            {
                if (perm == AdminWildcard) continue;
                declared.Should().Contain(perm,
                    $"role '{roleCode}' references undeclared permission '{perm}'");
            }
        }
    }

    [Fact]
    public void Admin_HoldsWildcardOnly()
    {
        var admin = GetRole("admin");
        var perms = admin.GetProperty("permissionCodes").EnumerateArray()
            .Select(p => p.GetString())
            .ToList();

        perms.Should().BeEquivalentTo(new[] { AdminWildcard });
    }

    [Fact]
    public void Viewer_HoldsOnlyReadCodes()
    {
        var viewer = GetRole("viewer");
        var perms = viewer.GetProperty("permissionCodes").EnumerateArray()
            .Select(p => p.GetString()!)
            .ToList();

        perms.Should().NotBeEmpty();
        perms.Should().OnlyContain(p => p.EndsWith(":read", StringComparison.Ordinal),
            "viewer must hold only :read codes");
    }

    [Fact]
    public void Author_DoesNotHoldPolicyPublish()
    {
        var author = GetRole("author");
        var perms = author.GetProperty("permissionCodes").EnumerateArray()
            .Select(p => p.GetString()!)
            .ToList();

        perms.Should().NotContain("andy-policies:policy:publish");
    }

    [Fact]
    public void Approver_DoesNotHoldPolicyAuthor()
    {
        var approver = GetRole("approver");
        var perms = approver.GetProperty("permissionCodes").EnumerateArray()
            .Select(p => p.GetString()!)
            .ToList();

        perms.Should().NotContain("andy-policies:policy:author");
    }

    [Fact]
    public void RbacSeedJson_IsSemanticProjectionOfRegistrationRbac()
    {
        var canonicalOpts = new JsonSerializerOptions { WriteIndented = false };

        var fromRegistration = LoadRbacBlock();
        var fromSeed = LoadRbacSeed();

        // Re-serialise both through the same options so whitespace /
        // key-order differences in the source files are normalised
        // away — this asserts semantic equality, not byte equality.
        var registrationCanonical = JsonSerializer.Serialize(fromRegistration, canonicalOpts);
        var seedCanonical = JsonSerializer.Serialize(fromSeed, canonicalOpts);

        seedCanonical.Should().Be(registrationCanonical,
            "config/rbac-seed.json must be a 1:1 projection of the rbac block in config/registration.json");
    }

    private static JsonElement GetRole(string code)
    {
        var rbac = LoadRbacBlock();
        return rbac.GetProperty("roles").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == code);
    }
}
