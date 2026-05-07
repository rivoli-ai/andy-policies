// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Request payload for <c>IPolicyService.UpdateDraftAsync</c>. Mutates the content
/// of an existing Draft version. The <c>Policy.Name</c> slug is never updated via
/// this path — that would require a separate endpoint (deliberately deferred).
/// <para>
/// <c>Rationale</c> is captured into the audit chain (P6.2) when supplied; the
/// <c>RationaleRequiredFilter</c> rejects empty values when the
/// <c>andy.policies.rationaleRequired</c> setting is on. Nullable for
/// backward compatibility (P9 follow-up #193).
/// </para>
/// </summary>
public record UpdatePolicyVersionRequest(
    string Summary,
    string Enforcement,
    string Severity,
    IReadOnlyList<string> Scopes,
    string RulesJson,
    string? Rationale = null);
