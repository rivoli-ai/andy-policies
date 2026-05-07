// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Request payload for <c>IPolicyService.CreateDraftAsync</c>. Creates both the
/// stable <c>Policy</c> row and its first <c>PolicyVersion</c> (version 1, Draft).
/// Enum fields accept any casing on input (parsed case-insensitively by the service).
/// <para>
/// <c>Rationale</c> is captured into the audit chain (P6.2) when supplied; the
/// <c>RationaleRequiredFilter</c> (P2.4) rejects empty values when the
/// <c>andy.policies.rationaleRequired</c> setting is on. Nullable for
/// backward compatibility — clients that pre-date the field continue to work
/// when the gate is off (P9 follow-up #193, 2026-05-07).
/// </para>
/// </summary>
public record CreatePolicyRequest(
    string Name,
    string? Description,
    string Summary,
    string Enforcement,
    string Severity,
    IReadOnlyList<string> Scopes,
    string RulesJson,
    string? Rationale = null);
