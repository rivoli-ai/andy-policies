// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown when request input fails syntactic or structural validation (bad enum
/// value, scope wildcard, malformed JSON, oversized rules, etc.). API layer maps
/// to HTTP 400 Bad Request.
/// </summary>
public class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }

    public ValidationException(string message, Exception inner) : base(message, inner) { }
}
