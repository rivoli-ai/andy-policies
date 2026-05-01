// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Policies.Api.Filters;

/// <summary>
/// Action filter that rejects override <i>write</i> endpoints
/// (propose, approve, revoke) with HTTP 403 when
/// <see cref="IExperimentalOverridesGate.IsEnabled"/> is <c>false</c>
/// (P5.4, story rivoli-ai/andy-policies#56). Stamps a structured
/// ProblemDetails body with <c>errorCode = "override.disabled"</c>
/// so clients can branch on the stable string.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads remain available</b> regardless of the gate — the filter
/// is applied per-action rather than per-controller, so
/// <c>POST /api/overrides</c>, <c>POST /api/overrides/{id}/approve</c>,
/// and <c>POST /api/overrides/{id}/revoke</c> opt-in by stamping
/// <c>[OverrideWriteGate]</c>; <c>GET</c> endpoints leave it off.
/// </para>
/// <para>
/// <b>Surface parity:</b> MCP and gRPC apply the same gate at their
/// own surface entrypoints (P5.6 / P5.7), each translating to the
/// surface's equivalent of HTTP 403. The shared error code
/// <c>override.disabled</c> is the contract.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class OverrideWriteGateAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>Stable error code surfaced in the ProblemDetails
    /// extensions and matched by the equivalent MCP/gRPC error
    /// envelopes.</summary>
    public const string ErrorCode = "override.disabled";

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var gate = context.HttpContext.RequestServices
            .GetRequiredService<IExperimentalOverridesGate>();

        if (!gate.IsEnabled)
        {
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Experimental overrides disabled",
                Detail =
                    "Override writes are disabled. Enable " +
                    ExperimentalOverridesGateMetadata.SettingKey +
                    " in andy-settings to proceed.",
                Type = "/problems/override-disabled",
                Instance = context.HttpContext.Request.Path,
                Extensions = { [nameof(ErrorCode)] = ErrorCode },
            };
            // Standard "errorCode" key — matches the typed-error
            // posture used elsewhere (PolicyExceptionHandler stamps it
            // verbatim, the Cockpit Angular client branches on it).
            problem.Extensions["errorCode"] = ErrorCode;
            context.Result = new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            return Task.CompletedTask;
        }

        return next();
    }
}

/// <summary>
/// Mirror of the andy-settings key string used by the
/// <c>ExperimentalOverridesGate</c> implementation, kept in the Api
/// layer so the filter doesn't take a project reference on
/// Infrastructure (the inversion would break the Clean Architecture
/// dependency direction).
/// </summary>
internal static class ExperimentalOverridesGateMetadata
{
    public const string SettingKey = "andy.policies.experimentalOverridesEnabled";
}
