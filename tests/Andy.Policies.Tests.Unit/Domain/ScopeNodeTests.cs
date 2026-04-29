// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Domain;

/// <summary>
/// P4.1 (#28) acceptance for the <see cref="ScopeNode"/> entity. Locks
/// down the default-field initialisers, the persisted-ordinal stability
/// of <see cref="ScopeType"/> (existing rows on disk depend on the
/// 0..5 layout), and the optional-parent shape that anchors root Org
/// nodes.
/// </summary>
public class ScopeNodeTests
{
    [Fact]
    public void New_ScopeNode_HasNonEmptyId()
    {
        var node = new ScopeNode();

        node.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void New_ScopeNode_HasUtcCreatedAt_AndUpdatedAt()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var node = new ScopeNode();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        node.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        node.UpdatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        node.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void New_ScopeNode_HasNullParent_ByDefault()
    {
        // Root Org nodes are the only ones with ParentId == null; the
        // default state lets us seed a root without explicitly clearing
        // a non-null Guid.
        var node = new ScopeNode();

        node.ParentId.Should().BeNull();
        node.Parent.Should().BeNull();
        node.Children.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ScopeType.Org, 0)]
    [InlineData(ScopeType.Tenant, 1)]
    [InlineData(ScopeType.Team, 2)]
    [InlineData(ScopeType.Repo, 3)]
    [InlineData(ScopeType.Template, 4)]
    [InlineData(ScopeType.Run, 5)]
    public void ScopeType_HasStablePersistedOrdinal(ScopeType value, int expected)
    {
        // EF persists via HasConversion<int>; renaming the enum members
        // is safe but reordering them would silently corrupt every row.
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void ScopeType_OrdinalDoublesAsCanonicalDepth()
    {
        // Service-layer invariant in P4.2: Depth == (int)Type. The unit
        // value here documents the ordinal mapping the seeder + service
        // layer rely on.
        ((int)ScopeType.Org).Should().Be(0);
        ((int)ScopeType.Run).Should().Be(5);
    }
}
