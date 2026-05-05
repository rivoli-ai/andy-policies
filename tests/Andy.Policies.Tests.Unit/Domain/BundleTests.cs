// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Domain;

/// <summary>
/// Unit tests for <see cref="Bundle"/> (P8.1, story
/// rivoli-ai/andy-policies#81). Pin the entity defaults — anything
/// behavioural belongs to <c>BundleService</c> (P8.2) or to the
/// <see cref="Andy.Policies.Infrastructure.Data.AppDbContext"/>
/// immutability sweep (covered by <c>BundleMigrationTests</c>).
/// </summary>
public class BundleTests
{
    [Fact]
    public void NewBundle_StateDefaultsToActive()
    {
        var b = new Bundle();
        b.State.Should().Be(BundleState.Active);
    }

    [Fact]
    public void NewBundle_IdIsNonEmptyGuid_ByDefault()
    {
        new Bundle().Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void NewBundle_CreatedAt_DefaultsToNowUtc()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var b = new Bundle();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        b.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void NewBundle_DeleteFields_DefaultToNull()
    {
        var b = new Bundle();
        b.DeletedAt.Should().BeNull();
        b.DeletedBySubjectId.Should().BeNull();
    }

    [Fact]
    public void NewBundle_SnapshotJson_DefaultsToEmptyObject()
    {
        // The default lets EF round-trip a half-built row in tests
        // without tripping the NOT NULL constraint while the snapshot
        // builder (P8.2) is filling it in. Production callers always
        // overwrite this before SaveChanges.
        new Bundle().SnapshotJson.Should().Be("{}");
    }

    [Fact]
    public void NewBundle_SnapshotHash_DefaultsToEmpty()
    {
        new Bundle().SnapshotHash.Should().BeEmpty();
    }

    [Fact]
    public void BundleStateEnum_HasExactlyTwoCanonicalValues()
    {
        // ADR posture: bundles are immutable snapshots. Adding a third
        // state would imply a lifecycle ladder this entity intentionally
        // does not have. If a third value lands without an ADR update,
        // this test fails loud.
        Enum.GetValues<BundleState>().Should().BeEquivalentTo(new[]
        {
            BundleState.Active,
            BundleState.Deleted,
        });
    }

    [Fact]
    public void BundleStateEnum_OrdinalValues_AreLoadBearing()
    {
        // The enum is persisted via HasConversion<string>() today, but
        // the ordinals are still the contract that a future migration
        // off string-storage would inherit; a re-numbering would
        // silently rewrite existing audit chain payloads.
        ((int)BundleState.Active).Should().Be(0);
        ((int)BundleState.Deleted).Should().Be(1);
    }
}
