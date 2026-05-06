// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Tests.E2E.EmbeddedSmoke;

/// <summary>
/// P10.4 (rivoli-ai/andy-policies#39) — environment-driven config for
/// the cross-service embedded smoke. Every endpoint, credential, and
/// orchestration flag comes from an env var so the same suite can run
/// (a) locally against <c>docker-compose.e2e.yml</c>, (b) under
/// Conductor Epic AO's harness against a <c>:9100/policies</c> proxy,
/// or (c) on CI against any other equivalent stack — without code
/// changes.
/// </summary>
/// <remarks>
/// Defaults track <c>docker-compose.e2e.yml</c>'s exposed ports so a
/// fresh local boot picks up sensible values. Every default is
/// overridable; tests must never bake URLs in.
/// </remarks>
public sealed class EmbeddedTestEnvironment
{
    public const string EnabledFlag = "E2E_ENABLED";
    public const string NoComposeFlag = "ANDY_POLICIES_E2E_NO_COMPOSE";

    public const string PoliciesBaseUrlVar = "ANDY_POLICIES_E2E_POLICIES_BASE_URL";
    public const string AuthBaseUrlVar = "ANDY_POLICIES_E2E_AUTH_BASE_URL";
    public const string ApiClientIdVar = "ANDY_POLICIES_E2E_API_CLIENT_ID";
    public const string ApiClientSecretVar = "ANDY_POLICIES_E2E_API_CLIENT_SECRET";
    public const string AudienceVar = "ANDY_POLICIES_E2E_AUDIENCE";
    public const string ComposeFileVar = "ANDY_POLICIES_E2E_COMPOSE_FILE";
    public const string ComposeWaitSecondsVar = "ANDY_POLICIES_E2E_COMPOSE_WAIT_SECONDS";

    private readonly Func<string, string?> _read;

    public EmbeddedTestEnvironment()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test seam — pass a custom env reader for unit tests.</summary>
    internal EmbeddedTestEnvironment(Func<string, string?> read)
    {
        _read = read;
    }

    /// <summary>
    /// Whether the suite is enabled at all. Mirrors the
    /// <c>EndToEndAuthSmokeTest</c> gate so dev-machine
    /// <c>dotnet test</c> runs (no Docker) skip silently.
    /// </summary>
    public bool IsEnabled =>
        string.Equals(_read(EnabledFlag), "1", StringComparison.Ordinal);

    /// <summary>
    /// Skip <c>docker compose up</c>/<c>down</c> from the fixture —
    /// stack is managed externally (Conductor Epic AO harness pattern).
    /// </summary>
    public bool SkipCompose =>
        string.Equals(_read(NoComposeFlag), "1", StringComparison.Ordinal);

    /// <summary>
    /// Base URL for andy-policies (path-prefix included if any).
    /// Defaults to the e2e compose's exposed andy-policies port.
    /// Conductor harness overrides to <c>http://localhost:9100/policies/</c>.
    /// </summary>
    public Uri PoliciesBaseUrl => ParseUri(PoliciesBaseUrlVar, "http://localhost:7113/");

    /// <summary>
    /// Base URL for andy-auth. Defaults to the e2e compose port.
    /// Conductor harness overrides to <c>http://localhost:9100/auth/</c>.
    /// </summary>
    public Uri AuthBaseUrl => ParseUri(AuthBaseUrlVar, "http://localhost:7002/");

    public string ApiClientId =>
        _read(ApiClientIdVar) ?? "andy-policies-api";

    public string ApiClientSecret =>
        _read(ApiClientSecretVar) ?? "e2e-test-secret-not-for-production";

    public string Audience =>
        _read(AudienceVar) ?? "urn:andy-policies-api";

    /// <summary>Compose file the fixture starts when <see cref="SkipCompose"/> is false.</summary>
    public string ComposeFile =>
        _read(ComposeFileVar) ?? "docker-compose.e2e.yml";

    /// <summary>
    /// Seconds to wait for the andy-policies <c>/health</c> endpoint
    /// to come up after <c>docker compose up</c>. Defaults to 90 —
    /// the e2e build (4 services, .NET + Node) can be slow on first
    /// run, especially in CI.
    /// </summary>
    public int ComposeWaitSeconds =>
        int.TryParse(_read(ComposeWaitSecondsVar), out var n) && n > 0
            ? n
            : 90;

    private Uri ParseUri(string var, string fallback)
    {
        var raw = _read(var);
        var s = string.IsNullOrWhiteSpace(raw) ? fallback : raw;
        return s.EndsWith('/') ? new Uri(s) : new Uri(s + "/");
    }
}
