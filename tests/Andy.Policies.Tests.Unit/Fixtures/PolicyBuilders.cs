// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Tests.Unit.Fixtures;

/// <summary>
/// Fluent builders for unit-test fixtures (P1.10, #80). Trade a small amount
/// of indirection for substantial savings in tests that arrange a multi-policy
/// catalog (e.g. list-filter coverage). Defaults match the production
/// invariants from ADR 0001 §6.
/// </summary>
internal static class PolicyBuilders
{
    public static Policy APolicy(string name = "test", Action<Policy>? mutate = null)
    {
        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedBySubjectId = "unit",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        mutate?.Invoke(policy);
        return policy;
    }

    public static PolicyVersion AVersion(
        Guid policyId,
        int number = 1,
        LifecycleState state = LifecycleState.Draft,
        EnforcementLevel enforcement = EnforcementLevel.Should,
        Severity severity = Severity.Moderate,
        IEnumerable<string>? scopes = null,
        Action<PolicyVersion>? mutate = null)
    {
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policyId,
            Version = number,
            State = state,
            Enforcement = enforcement,
            Severity = severity,
            Scopes = (scopes ?? Array.Empty<string>()).ToList(),
            Summary = "fixture",
            RulesJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "unit",
            ProposerSubjectId = "unit",
        };
        mutate?.Invoke(version);
        return version;
    }

    public static CreatePolicyRequest AMinimalCreateRequest(
        string name,
        string enforcement = "Must",
        string severity = "Critical",
        IEnumerable<string>? scopes = null) => new(
            Name: name,
            Description: null,
            Summary: "summary",
            Enforcement: enforcement,
            Severity: severity,
            Scopes: (scopes ?? Array.Empty<string>()).ToList(),
            RulesJson: "{}");
}
