// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Queries;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Single source of truth for policy read/write business rules. All four surfaces
/// (REST, MCP, gRPC, CLI) consume this interface — no business logic duplication
/// across surfaces per CLAUDE.md.
/// </summary>
/// <remarks>
/// This service performs no enforcement, no token issuance, no role storage, and
/// does not call andy-rbac. Edit-RBAC sits in the controller layer (Epic P7 —
/// rivoli-ai/andy-policies#7). Audit chain emission is Epic P6 — this service
/// may emit in-process domain events (out of scope for P1.4); audit rows are
/// persisted by the P6 chain writer.
/// </remarks>
public interface IPolicyService
{
    Task<IReadOnlyList<PolicyDto>> ListPoliciesAsync(ListPoliciesQuery query, CancellationToken ct = default);

    Task<PolicyDto?> GetPolicyAsync(Guid policyId, CancellationToken ct = default);

    Task<PolicyDto?> GetPolicyByNameAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyList<PolicyVersionDto>> ListVersionsAsync(Guid policyId, CancellationToken ct = default);

    Task<PolicyVersionDto?> GetVersionAsync(Guid policyId, Guid versionId, CancellationToken ct = default);

    Task<PolicyVersionDto?> GetActiveVersionAsync(Guid policyId, CancellationToken ct = default);

    Task<PolicyVersionDto> CreateDraftAsync(CreatePolicyRequest request, string subjectId, CancellationToken ct = default);

    Task<PolicyVersionDto> UpdateDraftAsync(Guid policyId, Guid versionId, UpdatePolicyVersionRequest request, string subjectId, CancellationToken ct = default);

    Task<PolicyVersionDto> BumpDraftFromVersionAsync(Guid policyId, Guid sourceVersionId, string subjectId, CancellationToken ct = default);
}
