// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Append + verify primitives over the catalog audit chain (P6.2,
/// story rivoli-ai/andy-policies#42). Every catalog mutation calls
/// <see cref="AppendAsync"/> exactly once with the action, target
/// entity, RFC 6902 field diff, and rationale; the implementation
/// reads the chain tail under a serializable transaction (with
/// Postgres advisory lock or SQLite semaphore), computes
/// <c>SHA-256(prevHash || canonicalJson(payload))</c>, and inserts
/// a new row. <see cref="VerifyChainAsync"/> walks rows ordered by
/// <c>Seq</c> and returns the first divergence — never throws on
/// tamper, only on transport-layer errors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomicity contract.</b> Callers should invoke
/// <see cref="AppendAsync"/> inside the same DbContext transaction
/// as their state change so that "entity updated" and "audit row
/// written" commit or roll back together. The chain implementation
/// itself opens a serializable inner transaction for the
/// tail-read + insert sequence.
/// </para>
/// <para>
/// <b>Genesis.</b> The first row's <c>PrevHash</c> is 32 zero
/// bytes (canonical-json serialised as 64 zero hex chars).
/// </para>
/// </remarks>
public interface IAuditChain
{
    /// <summary>
    /// Append a new event to the chain. Returns the persisted DTO
    /// with <see cref="AuditEventDto.Seq"/> + <see cref="AuditEventDto.HashHex"/>
    /// populated by the chain.
    /// </summary>
    Task<AuditEventDto> AppendAsync(AuditAppendRequest request, CancellationToken ct);

    /// <summary>
    /// Walk the chain in <c>Seq</c> order from
    /// <paramref name="fromSeq"/> (inclusive; defaults to 1) to
    /// <paramref name="toSeq"/> (inclusive; defaults to MAX(seq)).
    /// Returns <see cref="ChainVerificationResult.Valid"/>=false on
    /// the first row whose <c>PrevHash</c> doesn't match the
    /// previous row's computed hash, or whose own
    /// <c>Hash</c> doesn't match the SHA-256 of the prev-hash and
    /// canonical envelope. Never throws on divergence — that is a
    /// well-defined return value.
    /// </summary>
    Task<ChainVerificationResult> VerifyChainAsync(
        long? fromSeq, long? toSeq, CancellationToken ct);
}

/// <summary>
/// Inputs to <see cref="IAuditChain.AppendAsync"/>. Field types
/// mirror the entity, but the chain decides
/// <see cref="Domain.Entities.AuditEvent.Id"/>,
/// <see cref="Domain.Entities.AuditEvent.Seq"/>,
/// <see cref="Domain.Entities.AuditEvent.PrevHash"/>,
/// <see cref="Domain.Entities.AuditEvent.Hash"/>, and
/// <see cref="Domain.Entities.AuditEvent.Timestamp"/>.
/// </summary>
public sealed record AuditAppendRequest(
    string Action,
    string EntityType,
    string EntityId,
    string FieldDiffJson,
    string? Rationale,
    string ActorSubjectId,
    IReadOnlyList<string> ActorRoles);

/// <summary>
/// Outcome of <see cref="IAuditChain.VerifyChainAsync"/>.
/// </summary>
/// <param name="Valid">True if the inspected range is internally
/// consistent (every row's <c>PrevHash</c> matches the previous
/// row's <c>Hash</c>, and every row's <c>Hash</c> matches the
/// recomputed SHA-256).</param>
/// <param name="FirstDivergenceSeq">When <see cref="Valid"/> is
/// false, the <c>Seq</c> of the first row whose hashes don't line
/// up. Null when <see cref="Valid"/> is true.</param>
/// <param name="InspectedCount">Number of rows the verifier
/// walked. Useful for monitoring (a stall would surface as
/// <c>InspectedCount</c> shrinking).</param>
/// <param name="LastSeq">The largest <c>Seq</c> observed by the
/// verifier (i.e. the right end of the inspected range, or 0 when
/// the chain is empty).</param>
public sealed record ChainVerificationResult(
    bool Valid,
    long? FirstDivergenceSeq,
    long InspectedCount,
    long LastSeq);
