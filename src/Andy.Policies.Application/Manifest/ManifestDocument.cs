// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Manifest;

/// <summary>
/// Strongly-typed projection of <c>config/registration.json</c>. Loaded
/// at boot under embedded mode (P10.3, story
/// rivoli-ai/andy-policies#38) and POSTed to andy-auth, andy-rbac, and
/// andy-settings so each consumer can upsert its slice idempotently.
/// </summary>
/// <remarks>
/// Property names use camelCase to match the on-disk JSON without per-
/// property attributes. The DTOs intentionally only model the fields we
/// dispatch — unknown fields in the source manifest round-trip through
/// the raw <see cref="System.Text.Json.JsonElement"/> properties so a
/// schema extension upstream does not require a coordinated DTO update.
/// </remarks>
public sealed record ManifestDocument(
    ManifestService Service,
    ManifestAuth Auth,
    ManifestRbac Rbac,
    ManifestSettings Settings);

public sealed record ManifestService(
    string Name,
    string DisplayName,
    string Description);

public sealed record ManifestAuth(
    string Audience,
    ManifestApiClient ApiClient,
    ManifestWebClient WebClient);

public sealed record ManifestApiClient(
    string ClientId,
    string ClientType,
    string? ClientSecretEnvVar,
    string DisplayName,
    string Description,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> Scopes);

public sealed record ManifestWebClient(
    string ClientId,
    string ClientType,
    string DisplayName,
    string Description,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> PostLogoutRedirectUris);

public sealed record ManifestRbac(
    string ApplicationCode,
    string ApplicationName,
    string Description,
    IReadOnlyList<ManifestResourceType> ResourceTypes,
    IReadOnlyList<ManifestPermission> Permissions,
    IReadOnlyList<ManifestRole> Roles,
    string? TestUserRole);

public sealed record ManifestResourceType(
    string Code,
    string Name,
    bool SupportsInstances);

public sealed record ManifestPermission(
    string Code,
    string Name,
    string ResourceType);

public sealed record ManifestRole(
    string Code,
    string Name,
    string Description,
    bool IsSystem,
    IReadOnlyList<string> PermissionCodes);

public sealed record ManifestSettings(
    IReadOnlyList<ManifestSettingDefinition> Definitions);

public sealed record ManifestSettingDefinition(
    string Key,
    string DisplayName,
    string Description,
    string Category,
    string DataType,
    string DefaultValue,
    IReadOnlyList<string> AllowedScopes);
