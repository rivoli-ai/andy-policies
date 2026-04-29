// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown by <c>IScopeService.CreateAsync</c> when the proposed
/// <see cref="ScopeType"/> doesn't match the parent's slot in the
/// canonical Org→Tenant→Team→Repo→Template→Run ladder, or when a root
/// node is given a non-Org type (P4.2, story rivoli-ai/andy-policies#29).
/// API layer maps to HTTP 400 with
/// <c>errorCode = "scope.parent-type-mismatch"</c>.
/// </summary>
public sealed class InvalidScopeTypeException : ValidationException
{
    public InvalidScopeTypeException(string message) : base(message) { }
}
