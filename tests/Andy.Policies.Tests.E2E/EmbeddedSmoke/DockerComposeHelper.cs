// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;

namespace Andy.Policies.Tests.E2E.EmbeddedSmoke;

/// <summary>
/// Wraps <c>docker compose up -d --wait</c> / <c>down -v</c> for the
/// cross-service smoke fixture. Honors
/// <see cref="EmbeddedTestEnvironment.NoComposeFlag"/> — when set, both
/// <see cref="UpAsync"/> and <see cref="DownAsync"/> are no-ops, which
/// is the contract Conductor Epic AO's harness relies on (the harness
/// owns its own compose stack).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why shell out instead of using a Docker SDK?</b> The fixture has
/// to match what an operator would run by hand. Shelling guarantees
/// the same compose engine resolves images, networks, and `additional_contexts`
/// the same way locally and on CI. Adding a Docker SDK dependency would
/// also pull native bindings into our test stack for marginal benefit.
/// </para>
/// <para>
/// <b>--wait.</b> Compose's built-in healthcheck wait (since v2) blocks
/// until each service's healthcheck passes. The fixture still runs an
/// HTTP-level health probe afterwards as belt-and-braces, but
/// <c>--wait</c> means we don't see "connection refused" on the first
/// few requests after up.
/// </para>
/// </remarks>
public sealed class DockerComposeHelper
{
    private readonly EmbeddedTestEnvironment _env;
    private readonly string _workingDirectory;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;

    public DockerComposeHelper(EmbeddedTestEnvironment env, string workingDirectory)
        : this(env, workingDirectory, Process.Start)
    {
    }

    /// <summary>Test seam — substitute the process starter for unit tests.</summary>
    internal DockerComposeHelper(
        EmbeddedTestEnvironment env,
        string workingDirectory,
        Func<ProcessStartInfo, Process?> startProcess)
    {
        _env = env;
        _workingDirectory = workingDirectory;
        _startProcess = startProcess;
    }

    public bool DidStartCompose { get; private set; }

    public async Task UpAsync(CancellationToken ct)
    {
        if (_env.SkipCompose) return;

        await RunAsync(
            new[] { "compose", "-f", _env.ComposeFile, "up", "-d", "--wait" },
            timeoutSeconds: Math.Max(_env.ComposeWaitSeconds, 120),
            ct);
        DidStartCompose = true;
    }

    public async Task DownAsync(CancellationToken ct)
    {
        if (_env.SkipCompose) return;
        if (!DidStartCompose) return;

        await RunAsync(
            new[] { "compose", "-f", _env.ComposeFile, "down", "-v" },
            timeoutSeconds: 60,
            ct);
        DidStartCompose = false;
    }

    private async Task RunAsync(string[] args, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = _startProcess(psi)
            ?? throw new InvalidOperationException(
                $"Failed to start `docker {string.Join(' ', args)}` — is Docker installed?");

        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct);
        combined.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(combined.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException(
                $"`docker {string.Join(' ', args)}` exceeded {timeoutSeconds}s.");
        }

        if (process.ExitCode != 0)
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"`docker {string.Join(' ', args)}` exited {process.ExitCode}.\nstdout: {stdout}\nstderr: {stderr}");
        }
    }
}
