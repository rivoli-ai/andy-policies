// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Aggregate risk tier for a <see cref="ComplianceAssessment"/>
/// (rivoli-ai/conductor#1944 — TX F7.1). Wire-stable, lowercase on the
/// wire; the order is the severity order (a higher enum value is a
/// strictly worse tier). The tier is a deterministic fold over the
/// assessment's <see cref="ComplianceViolation"/>s — see
/// <c>IComplianceScorer</c> for the weight table — so the same violation
/// set always produces the same tier.
/// </summary>
/// <remarks>
/// Downstream consumers (rivoli-ai/conductor#1945 audit envelope, #1946
/// per-task cockpit render) bind to these string values; do not rename
/// or reorder without a contract bump.
/// </remarks>
[JsonConverter(typeof(RiskTierJsonConverter))]
public enum RiskTier
{
    /// <summary>No violations — the assessment is clean.</summary>
    None = 0,

    /// <summary>Minor, low-criticality manual-grade violations only.</summary>
    Low = 1,

    /// <summary>Moderate-criticality or accumulated low-grade violations.</summary>
    Medium = 2,

    /// <summary>High-criticality manual violations or a reject on a low-criticality policy.</summary>
    High = 3,

    /// <summary>A reject-firing violation on a critical-criticality policy.</summary>
    Critical = 4,
}

/// <summary>
/// Serialises <see cref="RiskTier"/> as a wire-stable lowercase string
/// (<c>none</c> / <c>low</c> / <c>medium</c> / <c>high</c> /
/// <c>critical</c>) on the wire — matching the <c>severity</c> field's
/// lowercase convention (rivoli-ai/conductor#1944). Reading is
/// case-insensitive so older/PascalCase payloads still bind.
/// </summary>
public sealed class RiskTierJsonConverter : JsonConverter<RiskTier>
{
    public override RiskTier Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return Enum.TryParse<RiskTier>(raw, ignoreCase: true, out var tier)
            ? tier
            : throw new JsonException($"Unknown risk tier '{raw}'.");
    }

    public override void Write(
        Utf8JsonWriter writer, RiskTier value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString().ToLowerInvariant());
}

/// <summary>
/// One typed compliance violation surfaced by a per-task / per-run
/// evaluation (rivoli-ai/conductor#1944). A violation is recorded for
/// each predicate that a firing decision rule depended on but which did
/// not <c>Pass</c> — i.e. an <see cref="Outcome"/> of
/// <c>fail</c> or <c>unevaluable</c>. <c>unevaluable</c> ("couldn't prove
/// it passed") is surfaced as a violation exactly like <c>fail</c>, never
/// as a silent pass — mirroring the conservative <c>RuleFires</c>
/// semantics on the plan-finalize path.
/// </summary>
/// <param name="PolicyKey">
/// Wire-stable string identifier of the policy that fired this violation
/// (the policy slug, e.g. <c>no-prod-deploy</c>) — never the internal
/// GUID. Matches what callers see in catalog listings and audit trails.
/// </param>
/// <param name="Predicate">
/// The camelCase predicate name (e.g. <c>noProductionDeploy</c>) that the
/// firing rule required but which did not pass.
/// </param>
/// <param name="Outcome">
/// The predicate's outcome on the wire: <c>fail</c> or <c>unevaluable</c>
/// (a <c>pass</c> is never a violation).
/// </param>
/// <param name="Decision">
/// The decision the firing rule emits — <c>reject</c> or <c>manual</c>.
/// A <c>reject</c>-grade violation outranks a <c>manual</c>-grade one in
/// scoring.
/// </param>
/// <param name="Reason">
/// Human-readable explanation produced by the predicate evaluation
/// (e.g. <c>"data unavailable"</c> for an unevaluable predicate).
/// </param>
public sealed record ComplianceViolation(
    string PolicyKey,
    string Predicate,
    string Outcome,
    string Decision,
    string Reason);

/// <summary>
/// Structured compliance assessment for a single task / agent run
/// (rivoli-ai/conductor#1944 — TX F7.1). Richer than the binary
/// <see cref="EvaluatePlanResponse.Decision"/> string: it carries the
/// individual <see cref="Violations"/> plus an aggregate
/// <see cref="RiskTier"/> and a numeric <see cref="RiskScore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Decision"/> string is kept for back-compat with
/// plan-finalize callers and is <em>derived</em> from the same firing
/// rules that produce the violations: a fired <c>reject</c> ⇒
/// <c>reject</c>; a fired <c>approve</c> ⇒ <c>approve</c>; otherwise the
/// default <c>manual</c>. The violations and risk tier are a new
/// projection layered on the same predicate trace — plan-finalize and
/// per-task share one evaluator core.
/// </para>
/// <para>
/// Designed for two downstream consumers: rivoli-ai/conductor#1945
/// (injects this payload into andy-docs under role:Audit) and #1946
/// (renders it per task in the cockpit). Hence stable
/// <see cref="ComplianceViolation.PolicyKey"/> strings, an ISO-8601
/// <see cref="EvaluatedAt"/>, and a wire-stable tier enum.
/// </para>
/// </remarks>
/// <param name="Decision">
/// Back-compat lowercase verdict <c>approve</c> / <c>manual</c> /
/// <c>reject</c> derived from the firing decision rules.
/// </param>
/// <param name="RiskTier">Aggregate risk tier (wire value lowercase).</param>
/// <param name="RiskScore">
/// Deterministic numeric risk score — the fold of violation weights.
/// 0 when there are no violations.
/// </param>
/// <param name="Violations">The typed violation list (empty when clean).</param>
/// <param name="Predicates">
/// The full predicate trace (every predicate evaluated against the
/// scoped view), identical in shape to the plan-finalize trace.
/// </param>
/// <param name="EvaluatedAt">ISO-8601 timestamp of evaluation.</param>
public sealed record ComplianceAssessment(
    string Decision,
    RiskTier RiskTier,
    int RiskScore,
    IReadOnlyList<ComplianceViolation> Violations,
    IReadOnlyList<PredicateResultDto> Predicates,
    DateTimeOffset EvaluatedAt);
