// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Domain;

/// <summary>
/// P3.1 (#19) acceptance for the <see cref="Binding"/> entity. Locks down
/// the default-field initialisers, the persisted-ordinal stability of
/// <see cref="BindingTargetType"/> and <see cref="BindStrength"/> (existing
/// rows on disk depend on these numerics), and the soft-delete tombstone
/// shape.
/// </summary>
public class BindingTests
{
    [Fact]
    public void New_Binding_HasNonEmptyId()
    {
        var binding = new Binding();

        binding.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void New_Binding_HasUtcCreatedAt()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var binding = new Binding();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        binding.CreatedAt.Should().BeOnOrAfter(before);
        binding.CreatedAt.Should().BeOnOrBefore(after);
        binding.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void New_Binding_DefaultsBindStrengthToRecommended()
    {
        var binding = new Binding();

        binding.BindStrength.Should().Be(BindStrength.Recommended);
    }

    [Fact]
    public void New_Binding_HasNullDeletedAtAndDeletedBy()
    {
        var binding = new Binding();

        binding.DeletedAt.Should().BeNull();
        binding.DeletedBySubjectId.Should().BeNull();
    }

    [Theory]
    [InlineData(BindingTargetType.Template, 1)]
    [InlineData(BindingTargetType.Repo, 2)]
    [InlineData(BindingTargetType.ScopeNode, 3)]
    [InlineData(BindingTargetType.Tenant, 4)]
    [InlineData(BindingTargetType.Org, 5)]
    public void BindingTargetType_HasStablePersistedOrdinal(BindingTargetType value, int expected)
    {
        // EF persists via HasConversion<int>; renaming the enum members is
        // safe but reordering them would silently corrupt every row on disk.
        ((int)value).Should().Be(expected);
    }

    [Theory]
    [InlineData(BindStrength.Mandatory, 1)]
    [InlineData(BindStrength.Recommended, 2)]
    public void BindStrength_HasStablePersistedOrdinal(BindStrength value, int expected)
    {
        ((int)value).Should().Be(expected);
    }
}
