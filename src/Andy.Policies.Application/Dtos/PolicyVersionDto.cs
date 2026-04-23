// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Wire-format projection of a <c>PolicyVersion</c>. Enum-shaped fields are
/// serialised in the casing required by ADR 0001 §6:
/// <list type="bullet">
///   <item><term>Enforcement</term><description>uppercase RFC 2119 tokens (<c>MUST</c> / <c>SHOULD</c> / <c>MAY</c>).</description></item>
///   <item><term>Severity</term><description>lowercase (<c>info</c> / <c>moderate</c> / <c>critical</c>).</description></item>
///   <item><term>State</term><description>PascalCase (<c>Draft</c> / <c>Active</c> / <c>WindingDown</c> / <c>Retired</c>) — matches DB storage and consumer-visible lifecycle labels.</description></item>
/// </list>
/// Service layer performs the casing conversion; controllers pass the DTO through unchanged.
/// </summary>
public record PolicyVersionDto(
    Guid Id,
    Guid PolicyId,
    int Version,
    string State,
    string Enforcement,
    string Severity,
    IReadOnlyList<string> Scopes,
    string Summary,
    string RulesJson,
    DateTimeOffset CreatedAt,
    string CreatedBySubjectId,
    string ProposerSubjectId);
