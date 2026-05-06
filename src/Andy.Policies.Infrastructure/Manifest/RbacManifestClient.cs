// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Manifest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.Manifest;

/// <summary>
/// POSTs the <c>rbac</c> block to andy-rbac's manifest ingest
/// endpoint. Idempotency is owned by the consumer: <c>applicationCode</c>
/// + <c>role.code</c> + <c>permission.code</c> are the upsert keys, so
/// re-submission updates names / descriptions but preserves subject
/// assignments. Endpoint URL is read at call time — see remarks on
/// <see cref="AuthManifestClient"/> for why.
/// </summary>
public sealed class RbacManifestClient : IRbacManifestClient
{
    public const string EndpointConfigKey = "AndyRbac:ManifestEndpoint";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<RbacManifestClient> _log;

    public RbacManifestClient(HttpClient http, IConfiguration config, ILogger<RbacManifestClient> log)
    {
        _http = http;
        _config = config;
        _log = log;
    }

    public async Task RegisterAsync(ManifestRbac rbac, CancellationToken ct)
    {
        var url = _config[EndpointConfigKey]
            ?? throw new ManifestRegistrationException("rbac",
                $"{EndpointConfigKey} is not configured. Set it via " +
                "AndyRbac__ManifestEndpoint=https://.../api/manifest in embedded mode.");
        try
        {
            using var resp = await _http
                .PostAsJsonAsync(url, rbac, Json, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, ct).ConfigureAwait(false);
                throw new ManifestRegistrationException(
                    "rbac",
                    $"andy-rbac manifest ingest returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
            }
            _log.LogInformation("RBAC manifest registered with andy-rbac at {Endpoint}.", url);
        }
        catch (HttpRequestException ex)
        {
            throw new ManifestRegistrationException(
                "rbac",
                $"andy-rbac manifest ingest transport error: {ex.Message}", ex);
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return "<unreadable>"; }
    }
}
