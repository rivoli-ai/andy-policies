// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Api.ExceptionHandlers;

/// <summary>
/// Maps <see cref="IPolicyService"/> exceptions to HTTP status codes per the P1.4
/// service contract:
/// <list type="bullet">
///   <item><see cref="ValidationException"/> → 400 Bad Request</item>
///   <item><see cref="NotFoundException"/> → 404 Not Found</item>
///   <item><see cref="ConflictException"/> → 409 Conflict</item>
///   <item><see cref="DbUpdateConcurrencyException"/> → 412 Precondition Failed</item>
/// </list>
/// Returns <c>false</c> for anything else so the default exception pipeline still handles it.
/// </summary>
public sealed class PolicyExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // Special-case RationaleRequiredException so the response carries the
        // typed ProblemDetails contract from P2.4 (#14): `type` points at the
        // rationale-required problem class, and `errors.rationale` is populated
        // for clients (Cockpit, CLI) that surface field-level validation
        // errors. Falls through to the generic 400 branch below if anything
        // changes — the base class is ValidationException.
        if (exception is RationaleRequiredException rrex)
        {
            var problem400 = new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["rationale"] = new[] { rrex.Message },
            })
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Rationale required",
                Detail = rrex.Message,
                Type = "/problems/rationale-required",
                Instance = httpContext.Request.Path,
            };
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(problem400, cancellationToken);
            return true;
        }

        // P4.4: tighten-only violation carries the offending ancestor
        // binding id + scope node id so admins can triage from the
        // error response without a follow-up query. We inject the
        // structured payload as ProblemDetails extension members.
        if (exception is TightenOnlyViolationException tvex)
        {
            var problem409 = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Tighten-only violation",
                Detail = tvex.Message,
                Type = "/problems/binding-tighten-only-violation",
                Instance = httpContext.Request.Path,
                Extensions =
                {
                    ["errorCode"] = "binding.tighten-only-violation",
                    ["offendingAncestorBindingId"] = tvex.Violation.OffendingAncestorBindingId,
                    ["offendingScopeNodeId"] = tvex.Violation.OffendingScopeNodeId,
                    ["offendingScopeDisplayName"] = tvex.Violation.OffendingScopeDisplayName,
                    ["policyKey"] = tvex.Violation.PolicyKey,
                },
            };
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            await httpContext.Response.WriteAsJsonAsync(problem409, cancellationToken);
            return true;
        }

        var (status, title) = exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
            NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            InvalidLifecycleTransitionException => (StatusCodes.Status409Conflict, "Invalid lifecycle transition"),
            ConcurrentPublishException => (StatusCodes.Status409Conflict, "Concurrent publish"),
            DbUpdateConcurrencyException => (StatusCodes.Status412PreconditionFailed, "Stale revision"),
            _ => (0, string.Empty),
        };

        if (status == 0) return false;

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception.Message,
            Type = $"https://httpstatuses.io/{status}",
            Instance = httpContext.Request.Path,
        };

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
