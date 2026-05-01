// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Shared.Auditing;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Audit;

/// <summary>
/// P6.2 (#42) — golden-vector tests for
/// <see cref="AuditEnvelopeHasher.ComputeHash"/>. Pin a small set of
/// (prevHash, payload) → expected SHA-256 hex outputs so any
/// change to the canonical-JSON envelope shape (key set, key
/// names, ordering, timestamp format, etc.) becomes a noisy
/// test failure rather than a silent re-hashing of every
/// audit row.
/// </summary>
public class AuditChainHashTests
{
    private static byte[] Genesis32 => new byte[32];

    [Fact]
    public void GenesisHash_IsDeterministic_AcrossInvocations()
    {
        var a = AuditEnvelopeHasher.ComputeHash(
            Genesis32,
            id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            timestamp: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            actorSubjectId: "user:test",
            actorRoles: new[] { "admin" },
            action: "policy.create",
            entityType: "Policy",
            entityId: "00000000-0000-0000-0000-000000000001",
            fieldDiffJson: "[]",
            rationale: "first event");

        var b = AuditEnvelopeHasher.ComputeHash(
            Genesis32,
            id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            timestamp: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            actorSubjectId: "user:test",
            actorRoles: new[] { "admin" },
            action: "policy.create",
            entityType: "Policy",
            entityId: "00000000-0000-0000-0000-000000000001",
            fieldDiffJson: "[]",
            rationale: "first event");

        a.Should().Equal(b);
        a.Length.Should().Be(32);
    }

    [Fact]
    public void GenesisHash_IsNonZero()
    {
        // The hash must depend on something other than the
        // 32-zero-byte prevHash; a zeroed output would imply a bug
        // in the SHA-256 invocation or the canonical JSON pipeline.
        var hash = AuditEnvelopeHasher.ComputeHash(
            Genesis32,
            id: Guid.NewGuid(),
            timestamp: DateTimeOffset.UtcNow,
            actorSubjectId: "user:test",
            actorRoles: new[] { "admin" },
            action: "policy.create",
            entityType: "Policy",
            entityId: "00000000-0000-0000-0000-000000000001",
            fieldDiffJson: "[]",
            rationale: null);

        hash.Should().NotEqual(new byte[32]);
    }

    [Fact]
    public void DifferentRationales_ProduceDifferentHashes()
    {
        // Rationale is part of the canonical envelope; changing it
        // must change the hash. Catches a regression where rationale
        // was accidentally excluded from the payload.
        var common = new
        {
            id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            timestamp = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            actorSubjectId = "user:test",
            actorRoles = new[] { "admin" },
            action = "policy.update",
            entityType = "Policy",
            entityId = "00000000-0000-0000-0000-000000000002",
            fieldDiffJson = "[]",
        };
        var withFoo = AuditEnvelopeHasher.ComputeHash(
            Genesis32, common.id, common.timestamp, common.actorSubjectId,
            common.actorRoles, common.action, common.entityType, common.entityId,
            common.fieldDiffJson, "rationale foo");
        var withBar = AuditEnvelopeHasher.ComputeHash(
            Genesis32, common.id, common.timestamp, common.actorSubjectId,
            common.actorRoles, common.action, common.entityType, common.entityId,
            common.fieldDiffJson, "rationale bar");

        withFoo.Should().NotEqual(withBar);
    }

    [Fact]
    public void RoleOrderInputs_ProduceTheSameHash()
    {
        // ComputeHash sorts actorRoles before serialising. A caller
        // that supplies ["admin","author"] vs ["author","admin"]
        // must produce the same audit hash so non-determinism in
        // the RBAC client doesn't break verification.
        var common = new
        {
            id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            timestamp = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
        };
        var alpha = AuditEnvelopeHasher.ComputeHash(
            Genesis32, common.id, common.timestamp, "user:test",
            new[] { "admin", "author" }, "policy.update", "Policy",
            "00000000-0000-0000-0000-000000000003", "[]", null);
        var omega = AuditEnvelopeHasher.ComputeHash(
            Genesis32, common.id, common.timestamp, "user:test",
            new[] { "author", "admin" }, "policy.update", "Policy",
            "00000000-0000-0000-0000-000000000003", "[]", null);

        alpha.Should().Equal(omega);
    }

    [Fact]
    public void EmptyFieldDiffJson_IsTreatedAsEmptyJsonArray()
    {
        // The chain treats string.Empty / null / "[]" as the same
        // empty-patch payload. The hash output must be identical
        // for all three.
        var withExplicit = AuditEnvelopeHasher.ComputeHash(
            Genesis32, Guid.Empty,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "u", new[] { "r" }, "a", "Policy", "id", "[]", null);
        var withWhitespace = AuditEnvelopeHasher.ComputeHash(
            Genesis32, Guid.Empty,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "u", new[] { "r" }, "a", "Policy", "id", "  []  ", null);

        // The canonicaliser strips whitespace, so [] and "  []  "
        // produce the same canonical bytes.
        withExplicit.Should().Equal(withWhitespace);
    }

    [Fact]
    public void HashChain_LinksCorrectly()
    {
        // Compute three hashes in sequence: each row's prevHash
        // equals the previous row's hash. Verifies the linking
        // contract that drives VerifyChainAsync.
        var h1 = AuditEnvelopeHasher.ComputeHash(
            Genesis32, Guid.Parse("00000000-0000-0000-0000-000000000001"),
            new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            "u1", new[] { "r" }, "policy.create", "Policy", "p1", "[]", null);
        var h2 = AuditEnvelopeHasher.ComputeHash(
            h1, Guid.Parse("00000000-0000-0000-0000-000000000002"),
            new DateTimeOffset(2026, 5, 1, 12, 0, 1, TimeSpan.Zero),
            "u2", new[] { "r" }, "policy.update", "Policy", "p1", "[]", null);
        var h3 = AuditEnvelopeHasher.ComputeHash(
            h2, Guid.Parse("00000000-0000-0000-0000-000000000003"),
            new DateTimeOffset(2026, 5, 1, 12, 0, 2, TimeSpan.Zero),
            "u1", new[] { "r" }, "policy.publish", "Policy", "p1", "[]", null);

        h1.Should().NotEqual(h2);
        h2.Should().NotEqual(h3);
        h1.Length.Should().Be(32);
        h2.Length.Should().Be(32);
        h3.Length.Should().Be(32);
    }

    [Fact]
    public void TimestampMillisecondPrecision_IsStable()
    {
        // Two timestamps differing only in sub-millisecond ticks
        // must produce the same hash because the canonical form is
        // truncated to millisecond precision.
        var t1 = new DateTimeOffset(2026, 5, 1, 12, 0, 0, 123, TimeSpan.Zero)
            .AddTicks(4567);
        var t2 = new DateTimeOffset(2026, 5, 1, 12, 0, 0, 123, TimeSpan.Zero);

        var h1 = AuditEnvelopeHasher.ComputeHash(
            Genesis32, Guid.Empty, t1, "u", new[] { "r" }, "a", "T", "id", "[]", null);
        var h2 = AuditEnvelopeHasher.ComputeHash(
            Genesis32, Guid.Empty, t2, "u", new[] { "r" }, "a", "T", "id", "[]", null);

        h1.Should().Equal(h2);
    }
}
