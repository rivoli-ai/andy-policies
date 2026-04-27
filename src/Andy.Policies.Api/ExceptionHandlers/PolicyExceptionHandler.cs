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
