// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Tests.E2E.EmbeddedSmoke;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.E2E;

/// <summary>
/// P10.4 (#39): exercises <see cref="AuditChainVerifier"/> in
/// isolation. Lives in Tests.Unit so the rules don't depend on a
/// running stack — synthetic <see cref="AuditEventDto"/>s are enough
/// to assert link integrity, seq monotonicity, genesis prevHash, and
/// the negative-tamper acceptance path.
/// </summary>
public class AuditChainVerifierTests
{
    [Fact]
    public void EmptyChain_FailsWithEmptyReason()
    {
        var (valid, reason) = AuditChainVerifier.Verify(Array.Empty<AuditEventDto>());

        valid.Should().BeFalse();
        reason.Should().Contain("empty");
    }

    [Fact]
    public void Genesis_With64ZeroPrevHash_IsAccepted()
    {
        var only = MakeEvent(seq: 1, prev: AuditChainVerifier.GenesisPrevHash, hash: "abc1");

        var (valid, _) = AuditChainVerifier.Verify(new[] { only }, expectFromGenesis: true);

        valid.Should().BeTrue();
    }

    [Fact]
    public void Genesis_WithNonZeroPrevHash_IsRejected()
    {
        var bogus = MakeEvent(seq: 1, prev: "deadbeef", hash: "abc1");

        var (valid, reason) = AuditChainVerifier.Verify(new[] { bogus }, expectFromGenesis: true);

        valid.Should().BeFalse();
        reason.Should().Contain("genesis");
    }

    [Fact]
    public void HappyPath_TwoLinkedEvents_IsAccepted()
    {
        var a = MakeEvent(seq: 1, prev: AuditChainVerifier.GenesisPrevHash, hash: "AAAA");
        var b = MakeEvent(seq: 2, prev: "AAAA", hash: "BBBB");

        var (valid, reason) = AuditChainVerifier.Verify(new[] { a, b }, expectFromGenesis: true);

        valid.Should().BeTrue(reason);
    }

    [Fact]
    public void TamperedPrevHash_BetweenLinks_IsRejected()
    {
        var a = MakeEvent(seq: 1, prev: AuditChainVerifier.GenesisPrevHash, hash: "AAAA");
        var b = MakeEvent(seq: 2, prev: "deadbeef", hash: "BBBB"); // wrong prev

        var (valid, reason) = AuditChainVerifier.Verify(new[] { a, b }, expectFromGenesis: true);

        valid.Should().BeFalse();
        reason.Should().Contain("chain link broken").And.Contain("seq=2");
    }

    [Fact]
    public void SeqGap_IsRejected()
    {
        var a = MakeEvent(seq: 1, prev: AuditChainVerifier.GenesisPrevHash, hash: "AAAA");
        var c = MakeEvent(seq: 3, prev: "AAAA", hash: "CCCC"); // skips seq=2

        var (valid, reason) = AuditChainVerifier.Verify(new[] { a, c }, expectFromGenesis: true);

        valid.Should().BeFalse();
        reason.Should().Contain("seq gap");
    }

    [Fact]
    public void SubRange_NoGenesisExpectation_LinksOnlyChecked()
    {
        // Same as the happy path but starts at seq=42 — the verifier
        // must not insist on seq=1 / genesis prevHash when called for
        // a sub-range page.
        var a = MakeEvent(seq: 42, prev: "f00d", hash: "AAAA");
        var b = MakeEvent(seq: 43, prev: "AAAA", hash: "BBBB");

        var (valid, reason) = AuditChainVerifier.Verify(new[] { a, b }, expectFromGenesis: false);

        valid.Should().BeTrue(reason);
    }

    [Fact]
    public void HashCaseInsensitive_Match_IsAccepted()
    {
        // Hex hashes from the API are documented as lowercase, but
        // OrdinalIgnoreCase compare keeps the verifier robust against
        // a JSON serializer flip.
        var a = MakeEvent(seq: 1, prev: AuditChainVerifier.GenesisPrevHash, hash: "aabb");
        var b = MakeEvent(seq: 2, prev: "AABB", hash: "ccdd");

        var (valid, _) = AuditChainVerifier.Verify(new[] { a, b }, expectFromGenesis: true);

        valid.Should().BeTrue();
    }

    private static AuditEventDto MakeEvent(long seq, string prev, string hash)
        => new(
            Id: Guid.NewGuid(),
            Seq: seq,
            PrevHashHex: prev,
            HashHex: hash,
            Timestamp: DateTimeOffset.UtcNow,
            ActorSubjectId: "u",
            ActorRoles: Array.Empty<string>(),
            Action: "x",
            EntityType: "X",
            EntityId: "id",
            FieldDiff: JsonDocument.Parse("[]").RootElement,
            Rationale: null);
}
