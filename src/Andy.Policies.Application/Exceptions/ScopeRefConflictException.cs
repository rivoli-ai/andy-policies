// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown by <c>IScopeService.CreateAsync</c> when the unique
/// <c>(Type, Ref)</c> index rejects an insert (P4.2, story
/// rivoli-ai/andy-policies#29). API layer maps to HTTP 409 Conflict.
/// </summary>
public sealed class ScopeRefConflictException : ConflictException
{
    public ScopeType Type { get; }

    public string Ref { get; }

    public ScopeRefConflictException(ScopeType type, string @ref)
        : base($"A scope node with Type={type} and Ref='{@ref}' already exists.")
    {
        Type = type;
        Ref = @ref;
    }

    public ScopeRefConflictException(ScopeType type, string @ref, Exception inner)
        : base($"A scope node with Type={type} and Ref='{@ref}' already exists.", inner)
    {
        Type = type;
        Ref = @ref;
    }
}
