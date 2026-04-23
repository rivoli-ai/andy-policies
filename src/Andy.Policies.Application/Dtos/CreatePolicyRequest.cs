// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Request payload for <c>IPolicyService.CreateDraftAsync</c>. Creates both the
/// stable <c>Policy</c> row and its first <c>PolicyVersion</c> (version 1, Draft).
/// Enum fields accept any casing on input (parsed case-insensitively by the service).
/// </summary>
public record CreatePolicyRequest(
    string Name,
    string? Description,
    string Summary,
    string Enforcement,
    string Severity,
    IReadOnlyList<string> Scopes,
    string RulesJson);
