// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Wire-format projection of
/// <see cref="Interfaces.ChainVerificationResult"/> (P6.5, story
/// rivoli-ai/andy-policies#45). Returned by
/// <c>GET /api/audit/verify</c> and the corresponding MCP /
/// gRPC surfaces.
/// </summary>
/// <param name="Valid">True when the inspected range is
/// internally consistent — every row's <c>PrevHash</c> matches
/// the previous row's computed hash, and every row's <c>Hash</c>
/// matches the recomputed SHA-256.</param>
/// <param name="FirstDivergenceSeq">When <see cref="Valid"/> is
/// false, the <c>Seq</c> of the first row whose hashes don't
/// line up. Null on a valid chain.</param>
/// <param name="InspectedCount">Number of rows the verifier
/// walked.</param>
/// <param name="LastSeq">Largest <c>Seq</c> observed by the
/// verifier (i.e. the right end of the inspected range, or 0
/// when the chain is empty).</param>
public sealed record ChainVerificationDto(
    bool Valid,
    long? FirstDivergenceSeq,
    long InspectedCount,
    long LastSeq);
