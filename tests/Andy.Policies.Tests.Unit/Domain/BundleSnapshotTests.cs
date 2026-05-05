// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using Andy.Policies.Domain.ValueObjects;
using Andy.Policies.Shared.Auditing;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Domain;

/// <summary>
/// Pin the canonical-serialization properties that
/// <see cref="BundleSnapshot"/> depends on for the
/// <c>SHA-256(canonicalJson(snapshot)) == Bundle.SnapshotHash</c>
/// invariant. Two logically equal snapshots must canonicalise to the
/// same byte sequence; any change to a field must change the hash.
/// P8.1 (rivoli-ai/andy-policies#81).
/// </summary>
/// <remarks>
/// The snapshot builder lands in P8.2; this test class only fixes the
/// data shape's serialization properties. P8.2 wires the per-entity
/// sort that makes "logically equal" deterministic across builds.
/// </remarks>
public class BundleSnapshotTests
{
    private static BundleSnapshot Sample() => new(
        SchemaVersion: "1",
        CapturedAt: DateTimeOffset.Parse("2026-05-05T17:00:00Z"),
        AuditTailHash: "abc123",
        Policies: new[]
        {
            new BundlePolicyEntry(
                PolicyId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name: "p1",
                PolicyVersionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Version: 1,
                Enforcement: "MUST",
                Severity: "critical",
                Scopes: new[] { "prod" },
                RulesJson: "{}",
                Summary: "first"),
        },
        Bindings: Array.Empty<BundleBindingEntry>(),
        Overrides: Array.Empty<BundleOverrideEntry>(),
        Scopes: Array.Empty<BundleScopeEntry>());

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public void CanonicalSerialization_ProducesByteIdenticalOutput_ForRecordEquality()
    {
        // Two records constructed from identical inputs must serialise
        // to the same bytes — this is the floor under the hash
        // determinism invariant. If a future field is added without a
        // canonical sort key, this test will catch the regression
        // before the migration ships.
        var a = Sample();
        var b = Sample();

        CanonicalJson.SerializeObject(a).Should().Equal(CanonicalJson.SerializeObject(b));
    }

    [Fact]
    public void CanonicalSerialization_IsStable_AcrossPropertyOrderingInJson()
    {
        // CanonicalJson lexicographically sorts object keys, so two
        // JSON inputs that differ only in property order must produce
        // identical output. This is the safety net for a future
        // System.Text.Json source generator that emits keys in a
        // different order.
        const string original = """
        {"capturedAt":"2026-05-05T17:00:00+00:00","auditTailHash":"x","policies":[],"bindings":[],"overrides":[],"scopes":[],"schemaVersion":"1"}
        """;
        const string reordered = """
        {"schemaVersion":"1","capturedAt":"2026-05-05T17:00:00+00:00","auditTailHash":"x","policies":[],"bindings":[],"overrides":[],"scopes":[]}
        """;

        var aBytes = CanonicalJson.Serialize(System.Text.Json.JsonDocument.Parse(original).RootElement);
        var bBytes = CanonicalJson.Serialize(System.Text.Json.JsonDocument.Parse(reordered).RootElement);

        aBytes.Should().Equal(bBytes);
    }

    [Fact]
    public void SnapshotHash_Is64HexLowercaseChars()
    {
        var hash = Sha256Hex(CanonicalJson.SerializeObject(Sample()));

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$",
            "consumers expect a fixed shape and verifiers (P8.7) compare " +
            "string-equal; mixed case or shorter hashes break the contract");
    }

    [Fact]
    public void SnapshotHash_Changes_WhenAnyPolicyEntryFieldChanges()
    {
        var baseline = Sha256Hex(CanonicalJson.SerializeObject(Sample()));

        var rotated = Sample() with
        {
            Policies = new[]
            {
                Sample().Policies[0] with { Summary = "different" },
            },
        };
        var rotatedHash = Sha256Hex(CanonicalJson.SerializeObject(rotated));

        rotatedHash.Should().NotBe(
            baseline,
            "a single-byte change inside a snapshot entry must shift the " +
            "hash — otherwise consumers can't detect tamper or partial " +
            "writes");
    }

    [Fact]
    public void SnapshotHash_Changes_WhenAuditTailHashChanges()
    {
        var baseline = Sha256Hex(CanonicalJson.SerializeObject(Sample()));
        var rotated = Sha256Hex(CanonicalJson.SerializeObject(
            Sample() with { AuditTailHash = "def456" }));

        rotated.Should().NotBe(baseline);
    }

    [Fact]
    public void SnapshotHash_Changes_WhenBindingAdded()
    {
        var baseline = Sha256Hex(CanonicalJson.SerializeObject(Sample()));
        var rotated = Sha256Hex(CanonicalJson.SerializeObject(
            Sample() with
            {
                Bindings = new[]
                {
                    new BundleBindingEntry(
                        BindingId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
                        PolicyVersionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        TargetType: "Repo",
                        TargetRef: "repo:rivoli-ai/x",
                        BindStrength: "Mandatory"),
                },
            }));

        rotated.Should().NotBe(baseline);
    }
}
