// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.Metrics;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Andy.Policies.Api.Filters;

/// <summary>
/// Per-action filter that enforces the bundle-pinning gate from P8.4
/// (rivoli-ai/andy-policies#84). When the action carries
/// <see cref="RequiresBundlePinAttribute"/> and
/// <see cref="IPinningPolicy.IsPinningRequired"/> is <c>true</c>, the
/// filter rejects requests that do not provide a non-empty
/// <c>?bundleId=</c> query parameter with a 400 Problem Details
/// response (<c>type</c> = <c>https://andy.local/problems/bundle-pin-required</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why per-action, not global middleware.</b> A blanket gate would
/// require an exhaustive negative allowlist (health, swagger, the
/// bundle endpoints themselves, audit, settings…) and one missing
/// entry would brick a public route. Positive annotation keeps the
/// gated set audit-able from the controller source.
/// </para>
/// <para>
/// <b>Pass-through cases:</b>
/// <list type="bullet">
///   <item>Action lacks the attribute → bypass.</item>
///   <item>Caller supplied a non-empty <c>bundleId</c> → bypass; the
///     action body decides whether the id is valid (a missing
///     bundle is a 404, not 400 — 400 is reserved for the
///     missing-parameter path).</item>
///   <item>Pinning is currently optional
///     (<see cref="IPinningPolicy.IsPinningRequired"/> = <c>false</c>) →
///     bypass.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class BundlePinningFilter : IAsyncActionFilter, IDisposable
{
    /// <summary>Stable URI used for the Problem Details <c>type</c>.
    /// Consumers can match programmatically without string-matching
    /// the detail message.</summary>
    public const string ProblemTypeUri = "https://andy.local/problems/bundle-pin-required";

    /// <summary>OpenTelemetry meter name. Increments
    /// <c>andy_policies_pinning_gate_decisions_total</c> with a
    /// <c>decision</c> label on every evaluation.</summary>
    public const string MeterName = "Andy.Policies.PinningGate";

    private readonly IPinningPolicy _pinning;
    private readonly Meter _meter;
    private readonly Counter<long> _decisionsCounter;

    public BundlePinningFilter(IPinningPolicy pinning)
    {
        _pinning = pinning;
        _meter = new Meter(MeterName);
        _decisionsCounter = _meter.CreateCounter<long>(
            name: "andy_policies_pinning_gate_decisions_total",
            description: "Bundle-pinning gate decisions, labelled by outcome.");
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var hasAttr = context.ActionDescriptor.EndpointMetadata
            .OfType<RequiresBundlePinAttribute>()
            .Any();
        if (!hasAttr)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var query = context.HttpContext.Request.Query;
        var hasBundle = query.TryGetValue("bundleId", out var values)
            && !string.IsNullOrWhiteSpace(values.ToString());
        if (hasBundle)
        {
            _decisionsCounter.Add(1, new KeyValuePair<string, object?>("decision", "pass"));
            await next().ConfigureAwait(false);
            return;
        }

        if (!_pinning.IsPinningRequired)
        {
            _decisionsCounter.Add(1, new KeyValuePair<string, object?>("decision", "pass-pinning-off"));
            await next().ConfigureAwait(false);
            return;
        }

        _decisionsCounter.Add(1, new KeyValuePair<string, object?>("decision", "block"));
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Pinning required",
            Detail = "Pinning required: pass ?bundleId=<guid>.",
            Type = ProblemTypeUri,
        };
        context.Result = new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }

    public void Dispose() => _meter.Dispose();
}
