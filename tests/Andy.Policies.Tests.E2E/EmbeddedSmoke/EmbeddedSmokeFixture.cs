// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace Andy.Policies.Tests.E2E.EmbeddedSmoke;

/// <summary>
/// xUnit class fixture for the cross-service embedded smoke (P10.4,
/// rivoli-ai/andy-policies#39). On <see cref="InitializeAsync"/>:
/// <list type="number">
///   <item><c>docker compose up -d --wait</c> (unless
///   <see cref="EmbeddedTestEnvironment.NoComposeFlag"/> is set) on
///   the configured compose file.</item>
///   <item>HTTP-level health probe on <c>{policies}/health</c>.</item>
///   <item><c>client_credentials</c> token from <c>{auth}/connect/token</c>
///   for the <c>andy-policies-api</c> client.</item>
///   <item>Configures an <see cref="HttpClient"/> with the bearer token
///   and the policies base URL set as <see cref="HttpClient.BaseAddress"/>.</item>
/// </list>
/// On <see cref="DisposeAsync"/>: <c>docker compose down -v</c> (unless
/// the no-compose flag is set, or the fixture didn't start compose
/// itself).
/// </summary>
/// <remarks>
/// <para>
/// <b>Skip semantics.</b> When <see cref="EmbeddedTestEnvironment.IsEnabled"/>
/// is false, both the boot and the token acquisition are no-ops and
/// <see cref="PoliciesClient"/> stays null. Tests gate on
/// <see cref="IsEnabled"/> and return early, mirroring the existing
/// <c>EndToEndAuthSmokeTest</c>. We could lift this to xUnit's Skip
/// attribute via Xunit.SkippableFact but the pattern is already
/// established and a return statement keeps the failure mode obvious
/// when env isn't set up.
/// </para>
/// <para>
/// <b>Working directory.</b> Compose is invoked from the repo root so
/// <c>docker-compose.e2e.yml</c> resolves and its
/// <c>additional_contexts</c> (e.g. <c>../andy-settings/artifacts</c>)
/// resolve relative to the repo. The fixture walks parent directories
/// from <see cref="AppContext.BaseDirectory"/> looking for
/// <c>Andy.Policies.sln</c>.
/// </para>
/// </remarks>
public sealed class EmbeddedSmokeFixture : IAsyncLifetime
{
    private readonly EmbeddedTestEnvironment _env = new();
    private readonly HttpClient _bootstrapHttp = new();
    private DockerComposeHelper? _compose;

    public EmbeddedTestEnvironment Env => _env;
    public bool IsEnabled => _env.IsEnabled;
    public string AdminToken { get; private set; } = string.Empty;

    /// <summary>HTTP client with bearer auth, base addressed at the policies surface. Null until <see cref="InitializeAsync"/> completes a successful boot.</summary>
    public HttpClient? PoliciesClient { get; private set; }

    public async Task InitializeAsync()
    {
        if (!_env.IsEnabled) return;

        var repoRoot = FindRepoRoot();
        _compose = new DockerComposeHelper(_env, repoRoot);
        await _compose.UpAsync(CancellationToken.None).ConfigureAwait(false);

        await WaitForHealthAsync(CancellationToken.None).ConfigureAwait(false);

        AdminToken = await AcquireClientCredentialsTokenAsync(CancellationToken.None).ConfigureAwait(false);

        PoliciesClient = new HttpClient { BaseAddress = _env.PoliciesBaseUrl };
        PoliciesClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminToken);
    }

    public async Task DisposeAsync()
    {
        PoliciesClient?.Dispose();
        _bootstrapHttp.Dispose();
        if (_compose is not null)
        {
            await _compose.DownAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task WaitForHealthAsync(CancellationToken ct)
    {
        var healthUrl = new Uri(_env.PoliciesBaseUrl, "health");
        var deadline = DateTime.UtcNow.AddSeconds(_env.ComposeWaitSeconds);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await _bootstrapHttp.GetAsync(healthUrl, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException ex) { last = ex; }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"andy-policies /health did not respond 2xx within {_env.ComposeWaitSeconds}s at {healthUrl}. " +
            $"Last error: {last?.Message ?? "(none)"}");
    }

    private async Task<string> AcquireClientCredentialsTokenAsync(CancellationToken ct)
    {
        var tokenUrl = new Uri(_env.AuthBaseUrl, "connect/token");
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", _env.ApiClientId),
            new KeyValuePair<string, string>("client_secret", _env.ApiClientSecret),
            new KeyValuePair<string, string>("scope", _env.Audience),
        });

        var resp = await _bootstrapHttp.PostAsync(tokenUrl, form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"andy-auth /connect/token returned {(int)resp.StatusCode} {resp.ReasonPhrase} for client {_env.ApiClientId}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("andy-auth returned no access_token in body.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Andy.Policies.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not locate Andy.Policies.sln walking up from {AppContext.BaseDirectory}.");
    }
}
