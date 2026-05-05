// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;

namespace Andy.Policies.Tools.GenerateRbacDocs;

/// <summary>
/// Reads <c>config/registration.json</c> and emits
/// <c>docs/reference/permission-catalog.md</c>. CI invokes
/// <c>--check</c> to fail builds on drift between the manifest and the
/// generated page. P7.7 (#65).
/// </summary>
internal static class Program
{
    private const string ManifestRelativePath = "config/registration.json";
    private const string OutputRelativePath = "docs/reference/permission-catalog.md";

    public static int Main(string[] args)
    {
        var checkMode = args.Any(a => a is "--check" or "-c");
        var repoRoot = ResolveRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine(
                "GenerateRbacDocs: could not locate repo root (no Andy.Policies.sln found " +
                "walking up from the working directory).");
            return 2;
        }

        var manifestPath = Path.Combine(repoRoot, ManifestRelativePath);
        var outputPath = Path.Combine(repoRoot, OutputRelativePath);
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"GenerateRbacDocs: manifest not found at {manifestPath}.");
            return 2;
        }

        string generated;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            generated = RbacCatalogGenerator.Generate(doc.RootElement);
        }
        catch (RbacCatalogGeneratorException ex)
        {
            Console.Error.WriteLine($"GenerateRbacDocs: {ex.Message}");
            return 2;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"GenerateRbacDocs: failed to parse {manifestPath}: {ex.Message}");
            return 2;
        }

        if (checkMode)
        {
            var current = File.Exists(outputPath) ? File.ReadAllText(outputPath) : string.Empty;
            // Normalise trailing newlines so a drift caused only by a
            // missing final newline doesn't fail CI; Generate always
            // emits a trailing \n.
            if (NormalizeNewlines(current) == NormalizeNewlines(generated))
            {
                return 0;
            }
            Console.Error.WriteLine(
                $"GenerateRbacDocs --check: {OutputRelativePath} is out of sync with " +
                $"{ManifestRelativePath}. Regenerate via:\n" +
                $"    dotnet run --project tools/GenerateRbacDocs");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, generated);
        Console.Out.WriteLine($"GenerateRbacDocs: wrote {OutputRelativePath} ({generated.Length} chars).");
        return 0;
    }

    private static string? ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Andy.Policies.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
}

/// <summary>
/// Pure markdown emitter for the permission catalog. Keeps the rendering
/// logic free of file IO so tests can pin tables, ordering, and admin
/// "*" handling against in-memory JSON inputs.
/// </summary>
internal static class RbacCatalogGenerator
{
    public const string GeneratedHeader =
        "<!-- GENERATED — do not edit. Source: config/registration.json. " +
        "Regenerate via `dotnet run --project tools/GenerateRbacDocs`. -->";

    public static string Generate(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("rbac", out var rbac))
        {
            throw new RbacCatalogGeneratorException("manifest is missing the 'rbac' object.");
        }

        var appCode = rbac.TryGetProperty("applicationCode", out var ac)
            ? ac.GetString() ?? throw new RbacCatalogGeneratorException("rbac.applicationCode is null.")
            : throw new RbacCatalogGeneratorException("rbac.applicationCode is missing.");

        var resourceTypes = ReadResourceTypes(rbac);
        var permissions = ReadPermissions(rbac);
        var roles = ReadRoles(rbac);

        var sb = new StringBuilder();
        sb.AppendLine("# Permission catalog");
        sb.AppendLine();
        sb.AppendLine(GeneratedHeader);
        sb.AppendLine();
        sb.Append("Application code: `").Append(appCode).AppendLine("`");
        sb.AppendLine();

        sb.AppendLine("## Resource types");
        sb.AppendLine();
        sb.AppendLine("| Code | Name | Supports instances |");
        sb.AppendLine("|------|------|--------------------|");
        foreach (var rt in resourceTypes)
        {
            sb.Append("| `").Append(rt.Code).Append("` | ")
              .Append(rt.Name).Append(" | ")
              .Append(rt.SupportsInstances ? "yes" : "no").AppendLine(" |");
        }
        sb.AppendLine();

        sb.AppendLine("## Permissions");
        sb.AppendLine();
        sb.AppendLine("| Code | Name | Resource type |");
        sb.AppendLine("|------|------|---------------|");
        foreach (var p in permissions)
        {
            sb.Append("| `").Append(p.Code).Append("` | ")
              .Append(p.Name).Append(" | `")
              .Append(p.ResourceType).AppendLine("` |");
        }
        sb.AppendLine();

        sb.AppendLine("## Roles");
        sb.AppendLine();
        sb.AppendLine("| Code | Name | Description | Permissions |");
        sb.AppendLine("|------|------|-------------|-------------|");
        foreach (var r in roles)
        {
            sb.Append("| `").Append(r.Code).Append("` | ")
              .Append(r.Name).Append(" | ")
              .Append(r.Description).Append(" | ")
              .Append(FormatPermissionList(r.PermissionCodes)).AppendLine(" |");
        }
        sb.AppendLine();

        sb.AppendLine("## Counts");
        sb.AppendLine();
        sb.Append("- Resource types: ").Append(resourceTypes.Count).AppendLine();
        sb.Append("- Permissions: ").Append(permissions.Count).AppendLine();
        sb.Append("- Roles: ").Append(roles.Count).AppendLine();

        return sb.ToString();
    }

    private static IReadOnlyList<ResourceType> ReadResourceTypes(JsonElement rbac)
    {
        if (!rbac.TryGetProperty("resourceTypes", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            throw new RbacCatalogGeneratorException("rbac.resourceTypes is missing or not an array.");
        }
        var list = new List<ResourceType>();
        foreach (var el in arr.EnumerateArray())
        {
            list.Add(new ResourceType(
                Code: ReadRequiredString(el, "code", "resourceTypes[].code"),
                Name: ReadRequiredString(el, "name", "resourceTypes[].name"),
                SupportsInstances: el.TryGetProperty("supportsInstances", out var si) && si.GetBoolean()));
        }
        return list;
    }

    private static IReadOnlyList<Permission> ReadPermissions(JsonElement rbac)
    {
        if (!rbac.TryGetProperty("permissions", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            throw new RbacCatalogGeneratorException("rbac.permissions is missing or not an array.");
        }
        var list = new List<Permission>();
        foreach (var el in arr.EnumerateArray())
        {
            list.Add(new Permission(
                Code: ReadRequiredString(el, "code", "permissions[].code"),
                Name: ReadRequiredString(el, "name", "permissions[].name"),
                ResourceType: ReadRequiredString(el, "resourceType", "permissions[].resourceType")));
        }
        return list;
    }

    private static IReadOnlyList<Role> ReadRoles(JsonElement rbac)
    {
        if (!rbac.TryGetProperty("roles", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            throw new RbacCatalogGeneratorException("rbac.roles is missing or not an array.");
        }
        var list = new List<Role>();
        foreach (var el in arr.EnumerateArray())
        {
            var perms = new List<string>();
            if (el.TryGetProperty("permissionCodes", out var pcArr) && pcArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pcArr.EnumerateArray())
                {
                    if (p.ValueKind == JsonValueKind.String)
                    {
                        perms.Add(p.GetString()!);
                    }
                }
            }
            list.Add(new Role(
                Code: ReadRequiredString(el, "code", "roles[].code"),
                Name: ReadRequiredString(el, "name", "roles[].name"),
                Description: el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                PermissionCodes: perms));
        }
        return list;
    }

    private static string ReadRequiredString(JsonElement el, string prop, string label)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String)
        {
            throw new RbacCatalogGeneratorException($"{label} is missing or not a string.");
        }
        return v.GetString()!;
    }

    private static string FormatPermissionList(IReadOnlyList<string> codes)
    {
        if (codes.Count == 1 && codes[0] == "*")
        {
            return "`*` (all permissions)";
        }
        if (codes.Count == 0)
        {
            return "(none)";
        }
        return string.Join(", ", codes.Select(c => $"`{c}`"));
    }

    private record ResourceType(string Code, string Name, bool SupportsInstances);

    private record Permission(string Code, string Name, string ResourceType);

    private record Role(string Code, string Name, string Description, IReadOnlyList<string> PermissionCodes);
}

/// <summary>
/// Thrown by <see cref="RbacCatalogGenerator.Generate"/> when the
/// manifest does not conform to the documented shape.
/// </summary>
internal sealed class RbacCatalogGeneratorException : Exception
{
    public RbacCatalogGeneratorException(string message) : base(message) { }
}
