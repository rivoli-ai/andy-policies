// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// A consumer-ready binding projection (P3.4, story
/// rivoli-ai/andy-policies#22). Joins the live <c>Binding</c> row to its
/// target <c>PolicyVersion</c> and stable <c>Policy</c> identity so a
/// caller (Conductor's ActionBus, andy-tasks per-task gates) gets enough
/// context to decide what to do without a second round-trip. Wire-format
/// casing follows ADR 0001 §6: <c>Enforcement</c> uppercase RFC 2119
/// tokens, <c>Severity</c> lowercase, <c>VersionState</c> PascalCase.
/// </summary>
public sealed record ResolvedBindingDto(
    Guid BindingId,
    Guid PolicyId,
    string PolicyName,
    Guid PolicyVersionId,
    int VersionNumber,
    string VersionState,
    string Enforcement,
    string Severity,
    IReadOnlyList<string> Scopes,
    BindStrength BindStrength);
