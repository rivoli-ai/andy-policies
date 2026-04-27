// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// P2.2 (#12) acceptance: <c>IsTransitionAllowed</c> returns true for exactly
/// the four transitions in the canonical matrix and false for all twelve
/// other 4x4 combinations. The full Cartesian product is asserted so a
/// matrix edit cannot accidentally widen what's allowed.
/// </summary>
public class LifecycleMatrixTests
{
    private static ILifecycleTransitionService NewService()
    {
        var db = InMemoryDbFixture.Create();
        return new LifecycleTransitionService(
            db,
            new RequireNonEmptyRationalePolicy(),
            new NoopDispatcher(),
            TimeProvider.System);
    }

    public static IEnumerable<object[]> AllowedTransitions() => new[]
    {
        new object[] { LifecycleState.Draft, LifecycleState.Active },
        new object[] { LifecycleState.Active, LifecycleState.WindingDown },
        new object[] { LifecycleState.Active, LifecycleState.Retired },
        new object[] { LifecycleState.WindingDown, LifecycleState.Retired },
    };

    public static IEnumerable<object[]> DeniedTransitions()
    {
        var allowed = new HashSet<(LifecycleState, LifecycleState)>
        {
            (LifecycleState.Draft, LifecycleState.Active),
            (LifecycleState.Active, LifecycleState.WindingDown),
            (LifecycleState.Active, LifecycleState.Retired),
            (LifecycleState.WindingDown, LifecycleState.Retired),
        };
        var values = Enum.GetValues<LifecycleState>();
        foreach (var from in values)
        {
            foreach (var to in values)
            {
                if (allowed.Contains((from, to)))
                {
                    continue;
                }
                yield return new object[] { from, to };
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void IsTransitionAllowed_ReturnsTrue_ForCanonicalMatrix(LifecycleState from, LifecycleState to)
    {
        var service = NewService();

        service.IsTransitionAllowed(from, to).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(DeniedTransitions))]
    public void IsTransitionAllowed_ReturnsFalse_ForEverythingElse(LifecycleState from, LifecycleState to)
    {
        var service = NewService();

        service.IsTransitionAllowed(from, to).Should().BeFalse();
    }

    [Fact]
    public void GetMatrix_ReturnsExactlyTheFourCanonicalRules()
    {
        var service = NewService();

        var matrix = service.GetMatrix();

        matrix.Should().HaveCount(4);
        matrix.Select(r => (r.From, r.To, r.Name)).Should().BeEquivalentTo(new[]
        {
            (LifecycleState.Draft, LifecycleState.Active, "Publish"),
            (LifecycleState.Active, LifecycleState.WindingDown, "WindDown"),
            (LifecycleState.Active, LifecycleState.Retired, "Retire"),
            (LifecycleState.WindingDown, LifecycleState.Retired, "Retire"),
        });
    }

    private sealed class NoopDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
            where TEvent : notnull => Task.CompletedTask;
    }
}
