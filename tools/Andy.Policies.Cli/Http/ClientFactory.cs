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
    /// <summary>
    /// Test-only seam: when set, <see cref="Create"/> wraps this handler
    /// instead of the production <see cref="HttpClientHandler"/>. Stored as
    /// <see cref="AsyncLocal{T}"/> so parallel xUnit runs don't bleed handlers
    /// across each other. Production callers never set this; production
    /// callers also never disable the test value (it's null by default).
    /// </summary>
    private static readonly AsyncLocal<HttpMessageHandler?> _handlerOverride = new();

    internal static IDisposable UseHandlerForTesting(HttpMessageHandler handler)
    {
        var previous = _handlerOverride.Value;
        _handlerOverride.Value = handler;
        return new HandlerScope(previous);
    }

    public static HttpClient Create(string apiUrl, string? token)
    {
        // ownsHandler: when an override is supplied (tests), the test owns the
        // lifetime of the handler — disposing the HttpClient must not dispose
        // it because the same handler is typically reused across calls.
        HttpMessageHandler handler;
        bool ownsHandler;
        if (_handlerOverride.Value is { } injected)
        {
            handler = injected;
            ownsHandler = false;
        }
        else
        {
            handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            ownsHandler = true;
        }

        var client = new HttpClient(handler, disposeHandler: ownsHandler) { BaseAddress = new Uri(apiUrl) };
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    private sealed class HandlerScope : IDisposable
    {
        private readonly HttpMessageHandler? _previous;
        public HandlerScope(HttpMessageHandler? previous) => _previous = previous;
        public void Dispose() => _handlerOverride.Value = _previous;
    }
}
