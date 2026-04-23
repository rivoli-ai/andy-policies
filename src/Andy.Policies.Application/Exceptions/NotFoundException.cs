// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown when a required entity could not be found (policy id, version id).
/// API layer maps to HTTP 404 Not Found. Read-shaped methods (<c>GetPolicyAsync</c>,
/// <c>GetVersionAsync</c>) prefer returning <c>null</c> over throwing; this exception
/// is for operations where "not found" is a precondition failure (e.g. bumping a
/// non-existent source version).
/// </summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }

    public NotFoundException(string message, Exception inner) : base(message, inner) { }
}
