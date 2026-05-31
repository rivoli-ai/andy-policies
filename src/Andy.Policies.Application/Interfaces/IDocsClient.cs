// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Thin client over the andy-docs agent-upload surface
/// (<c>POST /api/documents:put</c>, a.k.a. <c>docs.put</c>) — see
/// andy-docs <c>docs/agent-upload.md</c>. andy-policies uses it
/// (rivoli-ai/conductor#1945 — TX F7.2) to persist compliance
/// assessments + audit-chain export segments into andy-docs as
/// <c>role:Audit</c> documents linked to the goal/task they describe,
/// so audit/compliance is queryable across services.
/// </summary>
/// <remarks>
/// The call runs under andy-policies' M2M / run-scoped token (per
/// andy-docs Epic Y5). andy-docs does not validate the link target
/// exists — the trust boundary stays with andy-policies. The attach is
/// idempotent on <c>(documentId, targetType, targetId, role)</c>.
/// </remarks>
public interface IDocsClient
{
    /// <summary>
    /// Upload <paramref name="request"/> via the multipart
    /// <c>docs.put</c> route and return the resulting
    /// <see cref="DocsRef"/>. Throws on any non-success HTTP status or
    /// transport failure — the caller
    /// (<c>IComplianceAuditPublisher</c>) is responsible for the
    /// best-effort / degrade-without-blocking policy.
    /// </summary>
    Task<DocsRef> PutAsync(DocsPutRequest request, CancellationToken ct = default);
}
