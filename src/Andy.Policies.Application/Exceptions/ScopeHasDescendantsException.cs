// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Exceptions;

/// <summary>
/// Thrown by <c>IScopeService.DeleteAsync</c> when the node still has
/// at least one descendant (P4.2, story rivoli-ai/andy-policies#29).
/// API layer maps to HTTP 409 Conflict; consumers must walk the
/// subtree and delete leaves first.
/// </summary>
public sealed class ScopeHasDescendantsException : ConflictException
{
    public Guid ScopeNodeId { get; }

    public int ChildCount { get; }

    public ScopeHasDescendantsException(Guid scopeNodeId, int childCount)
        : base($"ScopeNode {scopeNodeId} cannot be deleted: it has {childCount} child{(childCount == 1 ? string.Empty : "ren")}.")
    {
        ScopeNodeId = scopeNodeId;
        ChildCount = childCount;
    }
}
