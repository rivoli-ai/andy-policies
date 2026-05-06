// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Application.Manifest;
using Microsoft.Extensions.Hosting;

namespace Andy.Policies.Infrastructure.Manifest;

/// <summary>
/// Production <see cref="IManifestLoader"/>: reads
/// <c>config/registration.json</c> off
/// <see cref="IHostEnvironment.ContentRootPath"/>. The Dockerfile copies
/// <c>config/</c> into the runtime stage (P10.3 reviewer-flagged fix —
/// see story rivoli-ai/andy-policies#38) so the file is present at
/// <c>/app/config/registration.json</c> in embedded mode.
/// </summary>
/// <remarks>
/// Throws <see cref="FileNotFoundException"/> if the manifest is
/// missing and <see cref="JsonException"/> if it cannot be parsed —
/// both are intentionally allowed to propagate so the hosted service
/// crashes the host on misconfiguration. Fail-loud beats a half-
/// configured embedded boot.
/// </remarks>
public sealed class FileManifestLoader : IManifestLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public FileManifestLoader(IHostEnvironment env)
    {
        _path = Path.Combine(env.ContentRootPath, "config", "registration.json");
    }

    // Test seam: anchor at an explicit path instead of the host
    // environment's content root. Used by FileManifestLoaderTests
    // to point at a temp file.
    internal FileManifestLoader(string path)
    {
        _path = path;
    }

    public async Task<ManifestDocument> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            throw new FileNotFoundException(
                $"Registration manifest not found at '{_path}'. " +
                "Embedded-mode boots must ship config/registration.json " +
                "in the runtime image (the Dockerfile copies the directory " +
                "into the runtime stage).",
                _path);
        }

        await using var stream = File.OpenRead(_path);
        var doc = await JsonSerializer.DeserializeAsync<ManifestDocument>(stream, Json, ct).ConfigureAwait(false);
        return doc ?? throw new InvalidOperationException(
            $"Registration manifest at '{_path}' deserialised to null.");
    }
}
