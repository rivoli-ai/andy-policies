// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Andy.Policies.Api.Filters;

/// <summary>
/// MVC action filter that rejects mutating requests whose DTOs
/// carry a rationale field but supply an empty / whitespace-only
/// value (P6.4, story rivoli-ai/andy-policies#44). Reads
/// <see cref="IRationalePolicy.IsRequired"/> on every request so
/// flipping <c>andy.policies.rationaleRequired</c> in andy-settings
/// takes effect on the next mutation without a restart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a filter instead of relying on the service-layer
/// check?</b> The service layer (P2's <c>LifecycleTransitionService</c>,
/// P3's <c>BindingService</c>, etc.) already raises
/// <see cref="Application.Exceptions.RationaleRequiredException"/>
/// when called with an empty rationale. The filter is a
/// defense-in-depth move: it catches the violation before any
/// service code runs, gives a uniform 400 ProblemDetails response
/// across all mutating endpoints, and means a future service
/// added without the existing check still inherits the
/// guarantee.
/// </para>
/// <para>
/// <b>Discovery.</b> The filter inspects every action argument
/// for a property either named exactly <c>Rationale</c> or
/// decorated with <see cref="RationaleAttribute"/>. Action
/// arguments without such a property are passed through (some
/// mutations, like a tombstone-by-id, legitimately have no
/// rationale concept; those endpoints can carry
/// <see cref="SkipRationaleCheckAttribute"/> for clarity).
/// </para>
/// <para>
/// <b>Method scope.</b> Only mutating HTTP methods are
/// inspected — GET / HEAD / OPTIONS pass through unconditionally
/// because the audit chain doesn't care about reads.
/// </para>
/// </remarks>
public sealed class RationaleRequiredFilter : IAsyncActionFilter
{
    /// <summary>Stable error code stamped on the ProblemDetails
    /// response. Matches the <c>errorCode</c> extension shape
    /// used elsewhere in the API.</summary>
    public const string ErrorCode = "rationale.required";

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var method = context.HttpContext.Request.Method;
        if (string.Equals(method, HttpMethods.Get, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, HttpMethods.Head, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, HttpMethods.Options, StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (HasSkipAttribute(context.ActionDescriptor))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var policy = context.HttpContext.RequestServices.GetService<IRationalePolicy>();
        if (policy is null || !policy.IsRequired)
        {
            await next().ConfigureAwait(false);
            return;
        }

        foreach (var (_, arg) in context.ActionArguments)
        {
            if (arg is null) continue;

            var rationale = TryReadRationale(arg);
            if (rationale is null) continue; // DTO doesn't carry a rationale field

            if (string.IsNullOrWhiteSpace(rationale.Value))
            {
                context.Result = BuildBadRequest(rationale.PropertyName, context.HttpContext.Request.Path);
                return;
            }
        }

        await next().ConfigureAwait(false);
    }

    private static bool HasSkipAttribute(Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor descriptor)
    {
        if (descriptor is not ControllerActionDescriptor controllerAction)
        {
            return false;
        }
        if (controllerAction.MethodInfo.GetCustomAttribute<SkipRationaleCheckAttribute>() is not null)
        {
            return true;
        }
        if (controllerAction.ControllerTypeInfo.GetCustomAttribute<SkipRationaleCheckAttribute>() is not null)
        {
            return true;
        }
        return false;
    }

    private static RationaleField? TryReadRationale(object arg)
    {
        // Properties decorated with [Rationale] win; otherwise the
        // canonical name "Rationale" is matched. Private setters
        // and init-only setters are both readable via GetValue —
        // we only need read access here.
        var props = arg.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead);
        var attributed = props.FirstOrDefault(p =>
            p.GetCustomAttribute<RationaleAttribute>() is not null);
        var canonical = attributed ?? props.FirstOrDefault(p =>
            string.Equals(p.Name, "Rationale", StringComparison.Ordinal));
        if (canonical is null || canonical.PropertyType != typeof(string))
        {
            return null;
        }
        var value = canonical.GetValue(arg) as string;
        return new RationaleField(canonical.Name, value);
    }

    private static BadRequestObjectResult BuildBadRequest(string propertyName, PathString path)
    {
        var problem = new ValidationProblemDetails(new Dictionary<string, string[]>
        {
            [propertyName.Length > 0
                ? char.ToLowerInvariant(propertyName[0]) + propertyName[1..]
                : propertyName] = new[] { "Rationale is required and may not be empty or whitespace." },
        })
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Rationale required",
            Type = "/problems/rationale-required",
            Instance = path,
            Detail =
                "andy.policies.rationaleRequired is enabled; include a non-empty rationale.",
        };
        problem.Extensions["errorCode"] = ErrorCode;
        return new BadRequestObjectResult(problem);
    }

    private sealed record RationaleField(string PropertyName, string? Value);
}
