// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Xunit;

namespace Andy.Policies.Tests.Unit.Domain;

public class DimensionDefaultsTests
{
    [Fact]
    public void NewPolicyVersion_DefaultsEnforcementToShould()
    {
        var version = new PolicyVersion();

        Assert.Equal(EnforcementLevel.Should, version.Enforcement);
    }

    [Fact]
    public void NewPolicyVersion_DefaultsSeverityToModerate()
    {
        var version = new PolicyVersion();

        Assert.Equal(Severity.Moderate, version.Severity);
    }

    [Fact]
    public void NewPolicyVersion_DefaultsScopesToEmpty()
    {
        var version = new PolicyVersion();

        Assert.NotNull(version.Scopes);
        Assert.Empty(version.Scopes);
    }

    [Fact]
    public void EnforcementLevel_HasExactlyThreeRfc2119Values()
    {
        var values = Enum.GetValues<EnforcementLevel>();

        Assert.Equal(3, values.Length);
        Assert.Contains(EnforcementLevel.May, values);
        Assert.Contains(EnforcementLevel.Should, values);
        Assert.Contains(EnforcementLevel.Must, values);
    }

    [Fact]
    public void Severity_HasExactlyThreeTriageTiers()
    {
        var values = Enum.GetValues<Severity>();

        Assert.Equal(3, values.Length);
        Assert.Contains(Severity.Info, values);
        Assert.Contains(Severity.Moderate, values);
        Assert.Contains(Severity.Critical, values);
    }

    [Fact]
    public void MutateDraftField_AppliesToDimensions_WhenDraft()
    {
        var version = new PolicyVersion();

        version.MutateDraftField(() =>
        {
            version.Enforcement = EnforcementLevel.Must;
            version.Severity = Severity.Critical;
            version.Scopes = new List<string> { "prod" };
        });

        Assert.Equal(EnforcementLevel.Must, version.Enforcement);
        Assert.Equal(Severity.Critical, version.Severity);
        Assert.Equal(new[] { "prod" }, version.Scopes);
    }

    [Fact]
    public void MutateDraftField_BlocksDimensionChanges_OnNonDraft()
    {
        var version = new PolicyVersion { State = LifecycleState.Active };

        Assert.Throws<InvalidOperationException>(() =>
            version.MutateDraftField(() => version.Enforcement = EnforcementLevel.Must));
    }
}
