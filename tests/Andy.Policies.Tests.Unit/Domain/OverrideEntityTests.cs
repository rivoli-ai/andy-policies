// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Domain;

/// <summary>
/// P5.1 (#49) acceptance for the <see cref="Override"/> entity.
/// Locks down the default-field initialisers, the persisted-string
/// stability of the three enums (existing rows on disk depend on
/// the textual form for the partial index), and the optional-replacement
/// shape that distinguishes Exempt from Replace.
/// </summary>
public class OverrideEntityTests
{
    [Fact]
    public void New_Override_HasNonEmptyId()
    {
        var ovr = new Override();

        ovr.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void New_Override_DefaultsStateToProposed()
    {
        var ovr = new Override();

        ovr.State.Should().Be(OverrideState.Proposed);
    }

    [Fact]
    public void New_Override_HasUtcProposedAt()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var ovr = new Override();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        ovr.ProposedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        ovr.ProposedAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void New_Override_HasNullApproverAndRevocationFields()
    {
        var ovr = new Override();

        ovr.ApproverSubjectId.Should().BeNull();
        ovr.ApprovedAt.Should().BeNull();
        ovr.RevocationReason.Should().BeNull();
        ovr.ReplacementPolicyVersionId.Should().BeNull();
    }

    [Theory]
    [InlineData(OverrideScopeKind.Principal, "Principal")]
    [InlineData(OverrideScopeKind.Cohort, "Cohort")]
    public void OverrideScopeKind_HasExpectedToStringForm(OverrideScopeKind value, string expected)
    {
        // EF persists via HasConversion<string>(); the string form is
        // load-bearing on disk for the composite index lookup.
        value.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(OverrideEffect.Exempt, "Exempt")]
    [InlineData(OverrideEffect.Replace, "Replace")]
    public void OverrideEffect_HasExpectedToStringForm(OverrideEffect value, string expected)
    {
        value.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(OverrideState.Proposed, "Proposed")]
    [InlineData(OverrideState.Approved, "Approved")]
    [InlineData(OverrideState.Revoked, "Revoked")]
    [InlineData(OverrideState.Expired, "Expired")]
    public void OverrideState_HasExpectedToStringForm(OverrideState value, string expected)
    {
        // Critical: the partial index "ix_overrides_expiry_approved"
        // filters on "State" = 'Approved' — a rename of this enum
        // member would silently invalidate the index without changing
        // any test that doesn't pin the string form.
        value.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(OverrideScopeKind.Principal, 0)]
    [InlineData(OverrideScopeKind.Cohort, 1)]
    public void OverrideScopeKind_HasStableOrdinalDefinition(OverrideScopeKind value, int expected)
    {
        // The ordinal is documentary (HasConversion<string>() means the
        // int value isn't persisted), but renaming or reordering would
        // break references in downstream consumers that bind to
        // `OverrideScopeKind.Principal` by name.
        ((int)value).Should().Be(expected);
    }

    [Theory]
    [InlineData(OverrideEffect.Exempt, 0)]
    [InlineData(OverrideEffect.Replace, 1)]
    public void OverrideEffect_HasStableOrdinalDefinition(OverrideEffect value, int expected)
    {
        ((int)value).Should().Be(expected);
    }
}
