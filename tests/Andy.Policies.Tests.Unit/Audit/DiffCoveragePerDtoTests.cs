// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Audit;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Audit;

/// <summary>
/// P6.8 (#50) — proves the
/// <see cref="JsonPatchDiffGenerator"/> produces meaningful
/// patches for every auditable catalog DTO. Each test verifies
/// (a) a no-op produces <c>"[]"</c> and (b) a single scalar
/// change produces a non-empty patch with the expected
/// <c>op</c> + camelCase <c>path</c>. This is the test gap the
/// epic explicitly calls out: every mutating service must be
/// able to call the generator on its DTO and get a useful
/// audit row.
/// </summary>
public class DiffCoveragePerDtoTests
{
    private static readonly JsonPatchDiffGenerator Generator = new();

    private static List<JsonElement> ParseOps(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => JsonDocument.Parse(e.GetRawText()).RootElement)
            .ToList();
    }

    [Fact]
    public void PolicyDto_ScalarChange_ProducesReplaceOp()
    {
        var before = new PolicyDto(
            Id: Guid.Empty,
            Name: "policy-a",
            Description: null,
            CreatedAt: DateTimeOffset.UnixEpoch,
            CreatedBySubjectId: "u",
            VersionCount: 1,
            ActiveVersionId: null);
        var after = before with { Name = "policy-b" };

        var patch = Generator.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().ContainSingle()
            .Which.GetProperty("path").GetString().Should().Be("/name");
        ops[0].GetProperty("op").GetString().Should().Be("replace");
    }

    [Fact]
    public void PolicyDto_NoChange_EmitsEmptyPatch()
    {
        var dto = new PolicyDto(
            Id: Guid.Empty,
            Name: "policy-a",
            Description: null,
            CreatedAt: DateTimeOffset.UnixEpoch,
            CreatedBySubjectId: "u",
            VersionCount: 1,
            ActiveVersionId: null);

        Generator.GenerateJsonPatch(dto, dto).Should().Be("[]");
    }

    [Fact]
    public void PolicyVersionDto_EnforcementChange_EmitsReplaceWithWireForm()
    {
        var pvid = Guid.NewGuid();
        var before = new PolicyVersionDto(
            Id: pvid,
            PolicyId: Guid.Empty,
            Version: 1,
            State: "Draft",
            Enforcement: "Should",
            Severity: "Moderate",
            Scopes: new List<string>(),
            Summary: "summary",
            RulesJson: "{}",
            CreatedAt: DateTimeOffset.UnixEpoch,
            CreatedBySubjectId: "u",
            ProposerSubjectId: "u");
        var after = before with { Enforcement = "Must" };

        var patch = Generator.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().ContainSingle();
        ops[0].GetProperty("op").GetString().Should().Be("replace");
        ops[0].GetProperty("path").GetString().Should().Be("/enforcement");
        ops[0].GetProperty("value").GetString().Should().Be("Must");
    }

    [Fact]
    public void BindingDto_TargetRefChange_EmitsReplaceOp()
    {
        var before = new BindingDto(
            Id: Guid.NewGuid(),
            PolicyVersionId: Guid.NewGuid(),
            TargetType: BindingTargetType.Repo,
            TargetRef: "repo:org/x",
            BindStrength: BindStrength.Mandatory,
            CreatedAt: DateTimeOffset.UnixEpoch,
            CreatedBySubjectId: "u",
            DeletedAt: null,
            DeletedBySubjectId: null);
        var after = before with { TargetRef = "repo:org/y" };

        var patch = Generator.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().ContainSingle();
        ops[0].GetProperty("path").GetString().Should().Be("/targetRef");
    }

    [Fact]
    public void BindingDto_NoChange_EmitsEmptyPatch()
    {
        var dto = new BindingDto(
            Id: Guid.NewGuid(),
            PolicyVersionId: Guid.NewGuid(),
            TargetType: BindingTargetType.Repo,
            TargetRef: "repo:org/x",
            BindStrength: BindStrength.Mandatory,
            CreatedAt: DateTimeOffset.UnixEpoch,
            CreatedBySubjectId: "u",
            DeletedAt: null,
            DeletedBySubjectId: null);

        Generator.GenerateJsonPatch(dto, dto).Should().Be("[]");
    }

    [Fact]
    public void OverrideDto_StateTransitionDiff_EmitsReplaceOp()
    {
        var pvid = Guid.NewGuid();
        var before = new OverrideDto(
            Id: Guid.NewGuid(),
            PolicyVersionId: pvid,
            ScopeKind: OverrideScopeKind.Principal,
            ScopeRef: "user:42",
            Effect: OverrideEffect.Exempt,
            ReplacementPolicyVersionId: null,
            ProposerSubjectId: "u:p",
            ApproverSubjectId: null,
            State: OverrideState.Proposed,
            ProposedAt: DateTimeOffset.UnixEpoch,
            ApprovedAt: null,
            ExpiresAt: DateTimeOffset.UnixEpoch.AddHours(1),
            Rationale: "r",
            RevocationReason: null);
        var after = before with
        {
            State = OverrideState.Approved,
            ApproverSubjectId = "u:a",
            ApprovedAt = DateTimeOffset.UnixEpoch.AddSeconds(30),
        };

        var patch = Generator.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().HaveCountGreaterOrEqualTo(2);
        ops.Select(o => o.GetProperty("path").GetString())
            .Should().Contain(new[] { "/state", "/approverSubjectId" });
    }

    [Fact]
    public void OverrideDto_NoChange_EmitsEmptyPatch()
    {
        var pvid = Guid.NewGuid();
        var dto = new OverrideDto(
            Id: Guid.NewGuid(),
            PolicyVersionId: pvid,
            ScopeKind: OverrideScopeKind.Principal,
            ScopeRef: "user:42",
            Effect: OverrideEffect.Exempt,
            ReplacementPolicyVersionId: null,
            ProposerSubjectId: "u:p",
            ApproverSubjectId: null,
            State: OverrideState.Proposed,
            ProposedAt: DateTimeOffset.UnixEpoch,
            ApprovedAt: null,
            ExpiresAt: DateTimeOffset.UnixEpoch.AddHours(1),
            Rationale: "r",
            RevocationReason: null);

        Generator.GenerateJsonPatch(dto, dto).Should().Be("[]");
    }

    [Fact]
    public void ScopeNodeDto_DisplayNameChange_EmitsReplaceOp()
    {
        var before = new ScopeNodeDto(
            Id: Guid.NewGuid(),
            ParentId: null,
            Type: ScopeType.Org,
            Ref: "org:rivoli",
            DisplayName: "Old Name",
            MaterializedPath: "org:rivoli",
            Depth: 0,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch);
        var after = before with { DisplayName = "New Name" };

        var patch = Generator.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().ContainSingle();
        ops[0].GetProperty("path").GetString().Should().Be("/displayName");
        ops[0].GetProperty("value").GetString().Should().Be("New Name");
    }

    [Fact]
    public void ScopeNodeDto_NoChange_EmitsEmptyPatch()
    {
        var dto = new ScopeNodeDto(
            Id: Guid.NewGuid(),
            ParentId: null,
            Type: ScopeType.Org,
            Ref: "org:rivoli",
            DisplayName: "Org",
            MaterializedPath: "org:rivoli",
            Depth: 0,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch);

        Generator.GenerateJsonPatch(dto, dto).Should().Be("[]");
    }

    [Fact]
    public void Diff_BeforeNullToFull_EmitsAllAdds()
    {
        var after = new PolicyDto(
            Id: Guid.NewGuid(),
            Name: "p",
            Description: "d",
            CreatedAt: DateTimeOffset.UnixEpoch,
            CreatedBySubjectId: "u",
            VersionCount: 1,
            ActiveVersionId: null);

        var patch = Generator.GenerateJsonPatch<PolicyDto>(null, after);

        var ops = ParseOps(patch);
        ops.Should().AllSatisfy(o =>
            o.GetProperty("op").GetString().Should().Be("add"));
    }

    [Fact]
    public void Diff_FullToAfterNull_EmitsAllRemoves()
    {
        var before = new PolicyDto(
            Id: Guid.NewGuid(),
            Name: "p",
            Description: "d",
            CreatedAt: DateTimeOffset.UnixEpoch,
            CreatedBySubjectId: "u",
            VersionCount: 1,
            ActiveVersionId: null);

        var patch = Generator.GenerateJsonPatch<PolicyDto>(before, null);

        var ops = ParseOps(patch);
        ops.Should().AllSatisfy(o =>
            o.GetProperty("op").GetString().Should().Be("remove"));
    }
}
