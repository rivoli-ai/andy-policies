// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Manifest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.Manifest;

/// <summary>
/// POSTs the <c>settings</c> block to andy-settings' manifest ingest
/// endpoint. Idempotency is owned by the consumer: <c>key</c> is the
/// upsert key. Re-submission updates metadata (display name, data
/// type, default) but does not clobber operator-set values. Endpoint
/// URL is read at call time — see remarks on <see cref="AuthManifestClient"/>.
/// </summary>
public sealed class SettingsManifestClient : ISettingsManifestClient
{
    public const string EndpointConfigKey = "AndySettings:ManifestEndpoint";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<SettingsManifestClient> _log;

    public SettingsManifestClient(HttpClient http, IConfiguration config, ILogger<SettingsManifestClient> log)
    {
        _http = http;
        _config = config;
        _log = log;
    }

    public async Task RegisterAsync(ManifestSettings settings, CancellationToken ct)
    {
        var url = _config[EndpointConfigKey]
            ?? throw new ManifestRegistrationException("settings",
                $"{EndpointConfigKey} is not configured. Set it via " +
                "AndySettings__ManifestEndpoint=https://.../api/manifest in embedded mode.");
        try
        {
            using var resp = await _http
                .PostAsJsonAsync(url, settings, Json, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, ct).ConfigureAwait(false);
                throw new ManifestRegistrationException(
                    "settings",
                    $"andy-settings manifest ingest returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
            }
            _log.LogInformation("Settings manifest registered with andy-settings at {Endpoint}.", url);
        }
        catch (HttpRequestException ex)
        {
            throw new ManifestRegistrationException(
                "settings",
                $"andy-settings manifest ingest transport error: {ex.Message}", ex);
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return "<unreadable>"; }
    }
}
