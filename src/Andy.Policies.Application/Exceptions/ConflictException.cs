// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown when a request would violate a uniqueness invariant (duplicate slug,
/// concurrent open draft, etc.). API layer maps to HTTP 409 Conflict.
/// </summary>
public class ConflictException : Exception
{
    public ConflictException(string message) : base(message) { }

    public ConflictException(string message, Exception inner) : base(message, inner) { }
}
