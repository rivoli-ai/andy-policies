// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Manifest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.Manifest;

/// <summary>
/// POSTs the <c>auth</c> block to andy-auth's manifest ingest endpoint
/// (rivoli-ai/andy-auth#41). Idempotency is owned by the consumer:
/// re-submitting the same <c>clientId</c> updates scopes / redirect
/// URIs / grant types but does not rotate persisted client secrets.
/// </summary>
/// <remarks>
/// The endpoint URL is read from <c>AndyAuth:ManifestEndpoint</c> at
/// call time rather than baked into the typed <see cref="HttpClient"/>'s
/// <see cref="HttpClient.BaseAddress"/>. Reason: the manifest hosted
/// service is registered unconditionally so the
/// <see cref="WebApplicationFactory{TEntryPoint}"/> in tests can
/// inject the URL via in-memory config (its
/// <c>ConfigureAppConfiguration</c> hook runs after Program.cs's
/// initial reads). Resolving lazily means Modes 1/2 — which leave
/// <c>Registration:AutoRegister</c> off and the URL unset — boot
/// without any manifest plumbing being touched.
/// </remarks>
public sealed class AuthManifestClient : IAuthManifestClient
{
    public const string EndpointConfigKey = "AndyAuth:ManifestEndpoint";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthManifestClient> _log;

    public AuthManifestClient(HttpClient http, IConfiguration config, ILogger<AuthManifestClient> log)
    {
        _http = http;
        _config = config;
        _log = log;
    }

    public async Task RegisterAsync(ManifestAuth auth, CancellationToken ct)
    {
        var url = _config[EndpointConfigKey]
            ?? throw new ManifestRegistrationException("auth",
                $"{EndpointConfigKey} is not configured. Set it via " +
                "AndyAuth__ManifestEndpoint=https://.../api/manifest in embedded mode.");
        try
        {
            using var resp = await _http
                .PostAsJsonAsync(url, auth, Json, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, ct).ConfigureAwait(false);
                throw new ManifestRegistrationException(
                    "auth",
                    $"andy-auth manifest ingest returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
            }
            _log.LogInformation("Auth manifest registered with andy-auth at {Endpoint}.", url);
        }
        catch (HttpRequestException ex)
        {
            throw new ManifestRegistrationException(
                "auth",
                $"andy-auth manifest ingest transport error: {ex.Message}", ex);
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return "<unreadable>"; }
    }
}
