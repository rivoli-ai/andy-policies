// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;

namespace Andy.Policies.Tests.E2E.EmbeddedSmoke;

/// <summary>
/// Client-side hash-chain link verifier for the audit surface. Used by
/// the cross-service smoke test (P10.4, story
/// rivoli-ai/andy-policies#39) to assert chain integrity over a list
/// of <see cref="AuditEventDto"/>s pulled from
/// <c>GET /api/audit</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it verifies.</b> Sequence monotonicity (no gaps, strict +1
/// step) and chain linkage — every event's <c>PrevHashHex</c> must
/// equal the previous event's <c>HashHex</c>, and the very first
/// event's <c>PrevHashHex</c> must be the genesis sentinel
/// (64 zero chars).
/// </para>
/// <para>
/// <b>What it does NOT verify.</b> The actual SHA-256 over canonical
/// payload bytes — that's the server's responsibility via
/// <c>GET /api/audit/verify</c>, which holds the canonical-JSON
/// algorithm and the source-of-truth payload. Recomputing on the
/// client would (a) require importing the Shared canonicaliser and
/// (b) drift silently if either side changes the algorithm. The smoke
/// test calls both — this verifier catches reordering / pagination
/// bugs / severed prevHash links; the server endpoint catches payload
/// tampering.
/// </para>
/// </remarks>
public static class AuditChainVerifier
{
    /// <summary>The 64-zero-char sentinel used as <c>PrevHashHex</c> for the genesis row.</summary>
    public const string GenesisPrevHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Verifies the chain link integrity of <paramref name="events"/>.
    /// Returns <c>(true, null)</c> when valid; otherwise
    /// <c>(false, reason)</c> describing the first fault.
    /// </summary>
    /// <param name="events">Events ordered ascending by
    /// <see cref="AuditEventDto.Seq"/>. The verifier does not re-sort
    /// — that's intentional, so a server that returns out-of-order
    /// pages is detected.</param>
    /// <param name="expectFromGenesis">When true (default for full-
    /// catalog scans), the first event must be seq=1 with the genesis
    /// prevHash. When false (sub-range checks), only adjacency is
    /// enforced.</param>
    public static (bool Valid, string? Reason) Verify(
        IReadOnlyList<AuditEventDto> events,
        bool expectFromGenesis = false)
    {
        if (events.Count == 0)
        {
            return (false, "audit chain is empty — no events to verify.");
        }

        for (var i = 0; i < events.Count; i++)
        {
            var current = events[i];
            if (i == 0)
            {
                if (expectFromGenesis)
                {
                    if (current.Seq != 1)
                        return (false, $"expected seq=1 at chain start, got seq={current.Seq}.");
                    if (!string.Equals(current.PrevHashHex, GenesisPrevHash, StringComparison.OrdinalIgnoreCase))
                        return (false, $"genesis row prevHash must be 64 zero chars, got '{current.PrevHashHex}'.");
                }
                continue;
            }

            var previous = events[i - 1];
            if (current.Seq != previous.Seq + 1)
            {
                return (false,
                    $"seq gap between events: previous seq={previous.Seq}, current seq={current.Seq} (expected {previous.Seq + 1}).");
            }
            if (!string.Equals(current.PrevHashHex, previous.HashHex, StringComparison.OrdinalIgnoreCase))
            {
                return (false,
                    $"chain link broken at seq={current.Seq}: prevHash='{current.PrevHashHex}' does not match previous hash='{previous.HashHex}'.");
            }
        }

        return (true, null);
    }
}
