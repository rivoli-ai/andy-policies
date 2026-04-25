// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;

namespace Andy.Policies.Cli.Http;

/// <summary>
/// Builds the bearer-authenticated <see cref="HttpClient"/> every CLI command uses.
/// The CLI is a thin REST client (P1.8) — no caching, no shared state. Dev-cert
/// validation bypass mirrors the scaffold and is acceptable because the CLI is
/// expected to point at a trusted endpoint.
/// </summary>
internal static class ClientFactory
{
    public static HttpClient Create(string apiUrl, string? token)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri(apiUrl) };
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }
}
