// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Queries;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// P1.10 (#80): coverage backfill for <c>IPolicyService.ListPoliciesAsync</c>'s
/// non-scope filter axes. Existing <c>PolicyServiceTests</c> covered the scope
/// path; namePrefix, enforcement, and severity filters were untested. Each
/// filter applies against the *active version* (highest non-Draft) per P1's
/// resolution rule, so the arrange phase mirrors the existing scope test:
/// create a draft, then flip <c>State</c> directly on the tracked entity to
/// simulate publishing without depending on the Epic P2 transition service.
/// </summary>
public class PolicyServiceListFilterTests
{
    private static async Task PublishAllAsync(Andy.Policies.Infrastructure.Data.AppDbContext db)
    {
        await foreach (var v in db.PolicyVersions.AsAsyncEnumerable())
        {
            v.State = LifecycleState.Active;
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ListPoliciesAsync_FiltersByNamePrefix()
    {
        using var db = InMemoryDbFixture.Create();
        var service = new PolicyService(db);
        await service.CreateDraftAsync(PolicyBuilders.AMinimalCreateRequest("alpha-1"), "sam");
        await service.CreateDraftAsync(PolicyBuilders.AMinimalCreateRequest("alpha-2"), "sam");
        await service.CreateDraftAsync(PolicyBuilders.AMinimalCreateRequest("beta-1"), "sam");
        await PublishAllAsync(db);

        var results = await service.ListPoliciesAsync(new ListPoliciesQuery(NamePrefix: "alpha"));

        results.Select(p => p.Name).Should().BeEquivalentTo(new[] { "alpha-1", "alpha-2" });
    }

    [Fact]
    public async Task ListPoliciesAsync_FiltersByEnforcement()
    {
        using var db = InMemoryDbFixture.Create();
        var service = new PolicyService(db);
        await service.CreateDraftAsync(
            PolicyBuilders.AMinimalCreateRequest("must-policy", enforcement: "Must"), "sam");
        await service.CreateDraftAsync(
            PolicyBuilders.AMinimalCreateRequest("should-policy", enforcement: "Should"), "sam");
        await service.CreateDraftAsync(
            PolicyBuilders.AMinimalCreateRequest("may-policy", enforcement: "May"), "sam");
        await PublishAllAsync(db);

        var results = await service.ListPoliciesAsync(new ListPoliciesQuery(Enforcement: "must"));

        results.Should().ContainSingle().Which.Name.Should().Be("must-policy");
    }

    [Fact]
    public async Task ListPoliciesAsync_FiltersBySeverity()
    {
        using var db = InMemoryDbFixture.Create();
        var service = new PolicyService(db);
        await service.CreateDraftAsync(
            PolicyBuilders.AMinimalCreateRequest("info", severity: "Info"), "sam");
        await service.CreateDraftAsync(
            PolicyBuilders.AMinimalCreateRequest("moderate", severity: "Moderate"), "sam");
        await service.CreateDraftAsync(
            PolicyBuilders.AMinimalCreateRequest("critical", severity: "Critical"), "sam");
        await PublishAllAsync(db);

        var results = await service.ListPoliciesAsync(new ListPoliciesQuery(Severity: "critical"));

        results.Should().ContainSingle().Which.Name.Should().Be("critical");
    }

    [Fact]
    public async Task ListPoliciesAsync_CombinesFilters_ConjunctiveAnd()
    {
        using var db = InMemoryDbFixture.Create();
        var service = new PolicyService(db);
        await service.CreateDraftAsync(
            PolicyBuilders.AMinimalCreateRequest("hot-1", enforcement: "Must", severity: "Critical"), "sam");
        await service.CreateDraftAsync(
            PolicyBuilders.AMinimalCreateRequest("hot-2", enforcement: "Must", severity: "Moderate"), "sam");
        await service.CreateDraftAsync(
            PolicyBuilders.AMinimalCreateRequest("cool-1", enforcement: "Should", severity: "Critical"), "sam");
        await PublishAllAsync(db);

        var results = await service.ListPoliciesAsync(new ListPoliciesQuery(
            Enforcement: "must",
            Severity: "critical"));

        results.Should().ContainSingle().Which.Name.Should().Be("hot-1");
    }

    [Theory]
    [InlineData("MUST")]
    [InlineData("must")]
    [InlineData("Must")]
    [InlineData("MuSt")]
    public async Task ListPoliciesAsync_EnforcementFilter_IsCaseInsensitive(string enforcementFilter)
    {
        using var db = InMemoryDbFixture.Create();
        var service = new PolicyService(db);
        await service.CreateDraftAsync(
            PolicyBuilders.AMinimalCreateRequest("locked", enforcement: "Must"), "sam");
        await PublishAllAsync(db);

        var results = await service.ListPoliciesAsync(new ListPoliciesQuery(Enforcement: enforcementFilter));

        results.Should().ContainSingle();
    }

    [Fact]
    public async Task ListPoliciesAsync_NamePrefix_AppliesToDraftPolicies_Too()
    {
        // Distinct from the other filter axes: namePrefix matches against the
        // stable Policy.Name and applies even when there's no active version.
        using var db = InMemoryDbFixture.Create();
        var service = new PolicyService(db);
        await service.CreateDraftAsync(PolicyBuilders.AMinimalCreateRequest("draft-stays-draft"), "sam");

        var results = await service.ListPoliciesAsync(new ListPoliciesQuery(NamePrefix: "draft-"));

        results.Should().ContainSingle().Which.Name.Should().Be("draft-stays-draft");
    }
}
