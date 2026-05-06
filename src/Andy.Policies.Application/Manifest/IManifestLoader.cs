// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Manifest;

/// <summary>
/// Reads <c>config/registration.json</c> off the host file system and
/// hands back a parsed <see cref="ManifestDocument"/>. The production
/// implementation (<c>FileManifestLoader</c>) anchors its lookup at
/// <c>IHostEnvironment.ContentRootPath</c>; tests substitute their own
/// loader to feed a fixture document.
/// </summary>
public interface IManifestLoader
{
    Task<ManifestDocument> LoadAsync(CancellationToken ct);
}
