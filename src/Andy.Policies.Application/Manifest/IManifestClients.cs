// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Manifest;

/// <summary>POST the <see cref="ManifestAuth"/> block to andy-auth's manifest endpoint.</summary>
public interface IAuthManifestClient
{
    Task RegisterAsync(ManifestAuth auth, CancellationToken ct);
}

/// <summary>POST the <see cref="ManifestRbac"/> block to andy-rbac's manifest endpoint.</summary>
public interface IRbacManifestClient
{
    Task RegisterAsync(ManifestRbac rbac, CancellationToken ct);
}

/// <summary>POST the <see cref="ManifestSettings"/> block to andy-settings' manifest endpoint.</summary>
public interface ISettingsManifestClient
{
    Task RegisterAsync(ManifestSettings settings, CancellationToken ct);
}
