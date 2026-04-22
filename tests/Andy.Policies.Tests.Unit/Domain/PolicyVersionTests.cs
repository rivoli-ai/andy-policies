// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Xunit;

namespace Andy.Policies.Tests.Unit.Domain;

public class PolicyVersionTests
{
    [Fact]
    public void State_DefaultsToDraft()
    {
        var version = new PolicyVersion();

        Assert.Equal(LifecycleState.Draft, version.State);
    }

    [Fact]
    public void Version_DefaultsToZero_UntilAssignedByService()
    {
        // The service layer (P1.4) assigns monotonic-starting-at-1 version numbers.
        // The entity itself has no opinion on the default — 0 is the uninitialised value.
        var version = new PolicyVersion();

        Assert.Equal(0, version.Version);
    }

    [Fact]
    public void RulesJson_DefaultsToEmptyObject()
    {
        var version = new PolicyVersion();

        Assert.Equal("{}", version.RulesJson);
    }

    [Fact]
    public void MutateDraftField_WhenStateIsDraft_AppliesChange()
    {
        var version = new PolicyVersion { Summary = "old" };

        version.MutateDraftField(() => version.Summary = "new");

        Assert.Equal("new", version.Summary);
    }

    [Theory]
    [InlineData(LifecycleState.Active)]
    [InlineData(LifecycleState.WindingDown)]
    [InlineData(LifecycleState.Retired)]
    public void MutateDraftField_WhenStateIsNotDraft_ThrowsInvalidOperationException(LifecycleState state)
    {
        var version = new PolicyVersion { State = state, Id = Guid.NewGuid() };

        var ex = Assert.Throws<InvalidOperationException>(
            () => version.MutateDraftField(() => version.Summary = "should fail"));

        Assert.Contains(version.Id.ToString(), ex.Message);
        Assert.Contains(state.ToString(), ex.Message);
    }

    [Fact]
    public void MutateDraftField_NullAction_ThrowsArgumentNullException()
    {
        var version = new PolicyVersion();

        Assert.Throws<ArgumentNullException>(() => version.MutateDraftField(null!));
    }

    [Fact]
    public void LifecycleTransitionFields_DefaultToNull()
    {
        var version = new PolicyVersion();

        Assert.Null(version.PublishedAt);
        Assert.Null(version.PublishedBySubjectId);
        Assert.Null(version.SupersededByVersionId);
    }

    [Fact]
    public void LifecycleState_EnumHasFourCanonicalValues()
    {
        // ADR 0002 pins the four-state machine. P2 relies on this exact set.
        var values = Enum.GetValues<LifecycleState>();

        Assert.Equal(4, values.Length);
        Assert.Contains(LifecycleState.Draft, values);
        Assert.Contains(LifecycleState.Active, values);
        Assert.Contains(LifecycleState.WindingDown, values);
        Assert.Contains(LifecycleState.Retired, values);
    }
}
