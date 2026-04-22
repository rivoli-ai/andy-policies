// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Xunit;

namespace Andy.Policies.Tests.Unit.Domain;

public class PolicyTests
{
    [Fact]
    public void Name_CanBeSetAndRead()
    {
        var policy = new Policy { Name = "no-prod" };

        Assert.Equal("no-prod", policy.Name);
    }

    [Fact]
    public void Versions_Collection_IsEmptyByDefault()
    {
        var policy = new Policy();

        Assert.NotNull(policy.Versions);
        Assert.Empty(policy.Versions);
    }

    [Fact]
    public void CreatedAt_DefaultsToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var policy = new Policy();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(policy.CreatedAt, before, after);
        Assert.Equal(TimeSpan.Zero, policy.CreatedAt.Offset);
    }

    [Fact]
    public void Description_IsOptional()
    {
        var policy = new Policy { Name = "high-risk" };

        Assert.Null(policy.Description);
    }
}
