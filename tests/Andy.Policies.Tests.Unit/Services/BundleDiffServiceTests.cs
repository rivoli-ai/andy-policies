// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Domain.ValueObjects;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Shared.Auditing;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="BundleDiffService"/> (P8.6, story
/// rivoli-ai/andy-policies#86). Drives the differ against
/// hand-built bundle pairs whose snapshots differ in known ways
/// so the emitted RFC-6902 patch is pinned op-by-op. Determinism
/// is the load-bearing assertion: two invocations on the same
/// pair must produce byte-identical patch JSON.
/// </summary>
public class BundleDiffServiceTests
{
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-05-06T18:00:00Z");

    private static BundleSnapshot SnapshotWith(
        IEnumerable<BundlePolicyEntry>? policies = null,
        IEnumerable<BundleBindingEntry>? bindings = null,
        IEnumerable<BundleOverrideEntry>? overrides = null) => new(
            SchemaVersion: "1",
            CapturedAt: CapturedAt,
            AuditTailHash: new string('0', 64),
            Policies: (policies ?? Array.Empty<BundlePolicyEntry>()).ToList(),
            Bindings: (bindings ?? Array.Empty<BundleBindingEntry>()).ToList(),
            Overrides: (overrides ?? Array.Empty<BundleOverrideEntry>()).ToList(),
            Scopes: Array.Empty<BundleScopeEntry>());

    private static BundlePolicyEntry Policy(
        Guid? id = null,
        string name = "p1",
        string enforcement = "Should",
        string severity = "Moderate") => new(
            PolicyId: id ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name: name,
            PolicyVersionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Version: 1,
            Enforcement: enforcement,
            Severity: severity,
            Scopes: Array.Empty<string>(),
            RulesJson: "{}",
            Summary: "fixture");

    private static BundleBindingEntry Binding(Guid? id = null, string targetRef = "repo:a") => new(
        BindingId: id ?? Guid.Parse("33333333-3333-3333-3333-333333333333"),
        PolicyVersionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        TargetType: "Repo",
        TargetRef: targetRef,
        BindStrength: "Recommended");

    private static async Task<(BundleDiffService svc, AppDbContext db, Guid fromId, Guid toId)>
        BuildPairAsync(BundleSnapshot from, BundleSnapshot to)
    {
        var db = InMemoryDbFixture.Create();

        async Task<Guid> AddBundle(BundleSnapshot snap, string name)
        {
            var canonical = CanonicalJson.SerializeObject(snap);
            var json = Encoding.UTF8.GetString(canonical);
            var hash = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
            var b = new Bundle
            {
                Id = Guid.NewGuid(),
                Name = name,
                CreatedAt = CapturedAt,
                CreatedBySubjectId = "test",
                SnapshotJson = json,
                SnapshotHash = hash,
                State = BundleState.Active,
            };
            db.Bundles.Add(b);
            await db.SaveChangesAsync();
            return b.Id;
        }

        var fromId = await AddBundle(from, "from");
        var toId = await AddBundle(to, "to");
        return (new BundleDiffService(db), db, fromId, toId);
    }

    [Fact]
    public async Task Diff_IdenticalSnapshots_ReturnsEmptyPatchArray()
    {
        var snap = SnapshotWith(policies: new[] { Policy() });
        var (svc, _, fromId, toId) = await BuildPairAsync(snap, snap);

        var result = await svc.DiffAsync(fromId, toId);

        result.Should().NotBeNull();
        result!.Rfc6902PatchJson.Should().Be(
            "[]",
            "logically identical snapshots emit no ops; canonical-JSON form " +
            "from P8.2 makes the byte-comparison trivially succeed");
        result.OpCount.Should().Be(0);
    }

    [Fact]
    public async Task Diff_PolicyEnforcementChanged_EmitsReplaceOp_OnEnforcementPath()
    {
        var pid = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        var from = SnapshotWith(policies: new[] { Policy(pid, enforcement: "Should") });
        var to = SnapshotWith(policies: new[] { Policy(pid, enforcement: "Must") });
        var (svc, _, fromId, toId) = await BuildPairAsync(from, to);

        var result = await svc.DiffAsync(fromId, toId);

        var patch = JsonDocument.Parse(result!.Rfc6902PatchJson);
        var ops = patch.RootElement.EnumerateArray().ToList();
        ops.Should().ContainSingle(
            o => o.GetProperty("op").GetString() == "replace"
              && o.GetProperty("path").GetString() == "/Policies/0/Enforcement"
              && o.GetProperty("value").GetString() == "Must",
            "the differ must address the field that actually changed; a path " +
            "of /Policies/0 (whole-policy replace) would lose the precision " +
            "auditors rely on for grep");
    }

    [Fact]
    public async Task Diff_BindingAdded_EmitsAddOp_AtEndOfArray()
    {
        var pid = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        var from = SnapshotWith(
            policies: new[] { Policy(pid) },
            bindings: new[] { Binding(targetRef: "repo:a") });
        var to = SnapshotWith(
            policies: new[] { Policy(pid) },
            bindings: new[]
            {
                Binding(targetRef: "repo:a"),
                Binding(id: Guid.Parse("99999999-9999-9999-9999-999999999999"), targetRef: "repo:b"),
            });
        var (svc, _, fromId, toId) = await BuildPairAsync(from, to);

        var result = await svc.DiffAsync(fromId, toId);

        var ops = JsonDocument.Parse(result!.Rfc6902PatchJson).RootElement.EnumerateArray().ToList();
        ops.Should().ContainSingle(o =>
            o.GetProperty("op").GetString() == "add"
            && o.GetProperty("path").GetString() == "/Bindings/1");
    }

    [Fact]
    public async Task Diff_BindingRemoved_EmitsRemoveOp_AtIndex()
    {
        var pid = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        var from = SnapshotWith(
            policies: new[] { Policy(pid) },
            bindings: new[]
            {
                Binding(targetRef: "repo:a"),
                Binding(id: Guid.Parse("99999999-9999-9999-9999-999999999999"), targetRef: "repo:b"),
            });
        var to = SnapshotWith(
            policies: new[] { Policy(pid) },
            bindings: new[] { Binding(targetRef: "repo:a") });
        var (svc, _, fromId, toId) = await BuildPairAsync(from, to);

        var result = await svc.DiffAsync(fromId, toId);

        var ops = JsonDocument.Parse(result!.Rfc6902PatchJson).RootElement.EnumerateArray().ToList();
        ops.Should().ContainSingle(o =>
            o.GetProperty("op").GetString() == "remove"
            && o.GetProperty("path").GetString() == "/Bindings/1");
    }

    [Fact]
    public async Task Diff_IsDeterministic_AcrossTwoInvocations()
    {
        var pid = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        var from = SnapshotWith(policies: new[] { Policy(pid, enforcement: "Should") });
        var to = SnapshotWith(policies: new[] { Policy(pid, enforcement: "Must") });
        var (svc, _, fromId, toId) = await BuildPairAsync(from, to);

        var first = await svc.DiffAsync(fromId, toId);
        var second = await svc.DiffAsync(fromId, toId);

        second!.Rfc6902PatchJson.Should().Be(
            first!.Rfc6902PatchJson,
            "diff determinism is the floor under reproducibility — two consumers " +
            "or two consecutive runs comparing the same pair must see byte-" +
            "identical patches");
    }

    [Fact]
    public async Task Diff_UnknownFromId_ReturnsNull()
    {
        var snap = SnapshotWith();
        var (svc, _, _, toId) = await BuildPairAsync(snap, snap);

        var result = await svc.DiffAsync(Guid.NewGuid(), toId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Diff_SoftDeletedBundle_ReturnsNull()
    {
        var snap = SnapshotWith();
        var (svc, db, fromId, toId) = await BuildPairAsync(snap, snap);

        // Tombstone the from bundle directly so the load filter
        // excludes it. Bypasses BundleService to avoid validating
        // rationale rules in this unit test.
        var tombstoned = await db.Bundles.FirstAsync(b => b.Id == fromId);
        tombstoned.State = BundleState.Deleted;
        tombstoned.DeletedAt = CapturedAt;
        tombstoned.DeletedBySubjectId = "op";
        await db.SaveChangesAsync();

        var result = await svc.DiffAsync(fromId, toId);

        result.Should().BeNull(
            "soft-deleted bundles are invisible to the resolution surface; " +
            "diff inherits that posture so a tombstoned bundle isn't a valid " +
            "comparison target");
    }

    [Fact]
    public async Task Diff_SnapshotHashes_AreEchoedInResult()
    {
        var snap = SnapshotWith(policies: new[] { Policy() });
        var (svc, db, fromId, toId) = await BuildPairAsync(snap, snap);
        var fromHash = (await db.Bundles.AsNoTracking().FirstAsync(b => b.Id == fromId)).SnapshotHash;
        var toHash = (await db.Bundles.AsNoTracking().FirstAsync(b => b.Id == toId)).SnapshotHash;

        var result = await svc.DiffAsync(fromId, toId);

        result!.FromSnapshotHash.Should().Be(fromHash);
        result.ToSnapshotHash.Should().Be(toHash);
        result.FromId.Should().Be(fromId);
        result.ToId.Should().Be(toId);
    }
}
