// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Single chokepoint for every lifecycle transition (P2.2, #12). Validates
/// the proposed move against the canonical matrix, runs the DB update inside
/// a serializable transaction, and dispatches in-process domain events
/// post-commit. REST (P2.3), MCP (P2.5), gRPC (P2.6), and CLI (P2.7) all
/// flow through this interface; controllers and tools never duplicate the
/// state-machine logic.
/// </summary>
public interface ILifecycleTransitionService
{
    bool IsTransitionAllowed(LifecycleState from, LifecycleState to);

    IReadOnlyList<LifecycleTransitionRule> GetMatrix();

    Task<PolicyVersionDto> TransitionAsync(
        Guid policyId,
        Guid versionId,
        LifecycleState target,
        string rationale,
        string actorSubjectId,
        CancellationToken ct = default);
}

/// <summary>
/// One row of the lifecycle transition matrix. <c>Name</c> is the human-readable
/// transition label exposed in API responses and CLI output (e.g. <c>Publish</c>,
/// <c>WindDown</c>, <c>Retire</c>).
/// </summary>
public sealed record LifecycleTransitionRule(LifecycleState From, LifecycleState To, string Name);
