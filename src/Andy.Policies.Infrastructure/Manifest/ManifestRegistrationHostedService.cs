// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Manifest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.Manifest;

/// <summary>
/// P10.3 (rivoli-ai/andy-policies#38) — under embedded mode, registers
/// andy-policies' OAuth client, RBAC application, and settings keys
/// with andy-auth, andy-rbac, and andy-settings on first boot. Gated
/// by <c>Registration:AutoRegister</c>; default off so Modes 1/2 keep
/// using the operator-driven <c>auth-seed.sql</c> + <c>rbac-seed.json</c>
/// path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order is load-bearing:</b> auth → rbac → settings. The CLI smoke
/// in P10.4 (story rivoli-ai/andy-policies#39) issues a token from
/// andy-auth using the <c>client_credentials</c> grant; the token
/// authenticates calls into andy-rbac (which needs the application
/// row to exist) and andy-settings (which needs the keys defined).
/// Reordering would surface as confusing 401/404 cascades during the
/// embedded smoke.
/// </para>
/// <para>
/// <b>Fail-loud semantics:</b> any exception inside <see cref="StartAsync"/>
/// propagates; the host crashes before Kestrel binds. A half-registered
/// embedded deployment is worse than an unregistered one — operators
/// get an immediate, loud failure instead of confusing 403s at runtime.
/// Mirrors the andy-rbac ADR-0001 posture (no silent degradation on
/// misconfiguration).
/// </para>
/// </remarks>
public sealed class ManifestRegistrationHostedService : IHostedService
{
    private readonly IConfiguration _config;
    private readonly IManifestLoader _loader;
    private readonly IAuthManifestClient _auth;
    private readonly IRbacManifestClient _rbac;
    private readonly ISettingsManifestClient _settings;
    private readonly ILogger<ManifestRegistrationHostedService> _log;

    public ManifestRegistrationHostedService(
        IConfiguration config,
        IManifestLoader loader,
        IAuthManifestClient auth,
        IRbacManifestClient rbac,
        ISettingsManifestClient settings,
        ILogger<ManifestRegistrationHostedService> log)
    {
        _config = config;
        _loader = loader;
        _auth = auth;
        _rbac = rbac;
        _settings = settings;
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (!IsAutoRegisterEnabled(_config))
        {
            _log.LogDebug("Registration:AutoRegister is disabled; skipping manifest dispatch.");
            return;
        }

        _log.LogInformation("Manifest auto-registration enabled; loading config/registration.json.");
        var manifest = await _loader.LoadAsync(ct).ConfigureAwait(false);

        // Order matters — see XML doc above. Each call throws
        // ManifestRegistrationException on failure; we let it
        // propagate so the host crashes before Kestrel binds.
        try
        {
            await _auth.RegisterAsync(manifest.Auth, ct).ConfigureAwait(false);
            await _rbac.RegisterAsync(manifest.Rbac, ct).ConfigureAwait(false);
            await _settings.RegisterAsync(manifest.Settings, ct).ConfigureAwait(false);
        }
        catch (ManifestRegistrationException ex)
        {
            _log.LogCritical(ex,
                "Manifest registration failed for block {Block}: aborting startup.",
                ex.Block);
            throw;
        }

        _log.LogInformation("Registration manifest applied: auth, rbac, settings.");
    }

    public Task StopAsync(CancellationToken _) => Task.CompletedTask;

    public static bool IsAutoRegisterEnabled(IConfiguration config)
    {
        var raw = config["Registration:AutoRegister"];
        return bool.TryParse(raw, out var run) && run;
    }
}
