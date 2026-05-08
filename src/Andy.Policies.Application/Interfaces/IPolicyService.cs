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

    /// <summary>
    /// Mark a Draft version as ready for an approver to review (#216).
    /// Sets <c>ReadyForReview = true</c> and emits a <c>policy.draft.proposed</c>
    /// audit event. Idempotent: re-proposing a draft already
    /// <c>ReadyForReview</c> succeeds without an additional audit event.
    /// </summary>
    /// <exception cref="Andy.Policies.Application.Exceptions.NotFoundException">
    /// No version matches <paramref name="versionId"/> for the given
    /// <paramref name="policyId"/>.</exception>
    /// <exception cref="Andy.Policies.Application.Exceptions.ConflictException">
    /// Version is not in <see cref="Andy.Policies.Domain.Enums.LifecycleState.Draft"/>;
    /// only Draft versions can be proposed.</exception>
    /// <exception cref="Andy.Policies.Application.Exceptions.ValidationException">
    /// Empty or oversized rationale (when the rationale gate is on).</exception>
    Task<PolicyVersionDto> ProposeDraftAsync(
        Guid policyId,
        Guid versionId,
        string? rationale,
        string subjectId,
        CancellationToken ct = default);

    /// <summary>
    /// Clear the ready-for-review flag on a Draft version (#216 — option (a)
    /// reject semantics: revert-to-draft, not terminal-state). Records the
    /// rejection rationale in the audit chain so the author can see why
    /// the proposal was bounced. Idempotent: rejecting a draft that is
    /// already not <c>ReadyForReview</c> is a no-op (no audit event).
    /// </summary>
    /// <exception cref="Andy.Policies.Application.Exceptions.NotFoundException">
    /// No version matches <paramref name="versionId"/> for the given
    /// <paramref name="policyId"/>.</exception>
    /// <exception cref="Andy.Policies.Application.Exceptions.ConflictException">
    /// Version is not in <see cref="Andy.Policies.Domain.Enums.LifecycleState.Draft"/>;
    /// reject is the proposal-time bounce only.</exception>
    /// <exception cref="Andy.Policies.Application.Exceptions.ValidationException">
    /// Empty rejection rationale — required for the audit trail.</exception>
    Task<PolicyVersionDto> RejectDraftAsync(
        Guid policyId,
        Guid versionId,
        string rationale,
        string subjectId,
        CancellationToken ct = default);

    /// <summary>
    /// List Draft versions where <c>ReadyForReview = true</c>, ordered by
    /// most-recently-created. Powers the approver inbox UI (#68).
    /// </summary>
    Task<IReadOnlyList<PolicyVersionDto>> ListPendingApprovalAsync(
        int skip,
        int take,
        CancellationToken ct = default);
}
