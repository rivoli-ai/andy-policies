// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="BindingResolver"/> (P3.4, story
/// rivoli-ai/andy-policies#22). Drives the resolver over EF Core InMemory
/// to lock down the four headline rules:
/// <list type="bullet">
///   <item>Retired versions are filtered out of the response.</item>
///   <item>Same-target/same-version dedup picks the stronger
///     <see cref="BindStrength"/> (Mandatory wins).</item>
///   <item>Match is byte-exact on <c>(TargetType, TargetRef)</c>.</item>
///   <item>Result ordering is deterministic: policy name ASC, then
///     version number DESC.</item>
/// </list>
/// </summary>
public class BindingResolverTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    private static (BindingResolver resolver, AppDbContext db) NewResolver()
    {
        var db = InMemoryDbFixture.Create();
        return (new BindingResolver(db), db);
    }

    private static async Task<PolicyVersion> SeedVersionAsync(
        AppDbContext db,
        string policyName,
        int versionNumber,
        LifecycleState state)
    {
        // Cache lookup by Name; reusing the same Policy across versions
        // mirrors the production aggregate shape (one Policy → many
        // PolicyVersions).
        var policy = await db.Policies.FirstOrDefaultAsync(p => p.Name == policyName);
        if (policy is null)
        {
            policy = PolicyBuilders.APolicy(name: policyName);
            db.Policies.Add(policy);
        }
        var version = PolicyBuilders.AVersion(policy.Id, number: versionNumber, state: state);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return version;
    }

    private static async Task<Binding> SeedBindingAsync(
        AppDbContext db,
        Guid policyVersionId,
        BindingTargetType targetType,
        string targetRef,
        BindStrength strength,
        DateTimeOffset? createdAt = null)
    {
        var binding = new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = policyVersionId,
            TargetType = targetType,
            TargetRef = targetRef,
            BindStrength = strength,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            CreatedBySubjectId = "test",
        };
        db.Bindings.Add(binding);
        await db.SaveChangesAsync();
        return binding;
    }

    [Fact]
    public async Task Resolve_OnEmptyDb_ReturnsZeroCount()
    {
        var (resolver, _) = NewResolver();

        var response = await resolver.ResolveExactAsync(BindingTargetType.Repo, "repo:none/missing");

        response.Count.Should().Be(0);
        response.Bindings.Should().BeEmpty();
        response.TargetType.Should().Be(BindingTargetType.Repo);
        response.TargetRef.Should().Be("repo:none/missing");
    }

    [Fact]
    public async Task Resolve_FiltersOutRetiredVersion()
    {
        var (resolver, db) = NewResolver();
        var live = await SeedVersionAsync(db, "live-pol", 1, LifecycleState.Active);
        var retired = await SeedVersionAsync(db, "live-pol", 2, LifecycleState.Retired);
        const string target = "template:abc";
        await SeedBindingAsync(db, live.Id, BindingTargetType.Template, target, BindStrength.Mandatory);
        await SeedBindingAsync(db, retired.Id, BindingTargetType.Template, target, BindStrength.Mandatory);

        var response = await resolver.ResolveExactAsync(BindingTargetType.Template, target);

        response.Bindings.Should().ContainSingle()
            .Which.PolicyVersionId.Should().Be(live.Id);
    }

    [Fact]
    public async Task Resolve_DedupsSameVersion_PrefersMandatory()
    {
        var (resolver, db) = NewResolver();
        var version = await SeedVersionAsync(db, "dup-pol", 1, LifecycleState.Active);
        const string target = "tenant:abc";
        await SeedBindingAsync(db, version.Id, BindingTargetType.Tenant, target,
            BindStrength.Recommended, createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        await SeedBindingAsync(db, version.Id, BindingTargetType.Tenant, target,
            BindStrength.Mandatory, createdAt: DateTimeOffset.UtcNow);

        var response = await resolver.ResolveExactAsync(BindingTargetType.Tenant, target);

        response.Bindings.Should().ContainSingle()
            .Which.BindStrength.Should().Be(BindStrength.Mandatory);
    }

    [Fact]
    public async Task Resolve_DedupsSameVersion_TwoRecommended_KeepsEarliest()
    {
        var (resolver, db) = NewResolver();
        var version = await SeedVersionAsync(db, "tie-pol", 1, LifecycleState.Active);
        var earlier = await SeedBindingAsync(db, version.Id, BindingTargetType.Org,
            "org:acme", BindStrength.Recommended, DateTimeOffset.UtcNow.AddMinutes(-5));
        await SeedBindingAsync(db, version.Id, BindingTargetType.Org,
            "org:acme", BindStrength.Recommended, DateTimeOffset.UtcNow);

        var response = await resolver.ResolveExactAsync(BindingTargetType.Org, "org:acme");

        response.Bindings.Should().ContainSingle()
            .Which.BindingId.Should().Be(earlier.Id);
    }

    [Fact]
    public async Task Resolve_MatchIsByteExact_NoCaseFolding()
    {
        var (resolver, db) = NewResolver();
        var v1 = await SeedVersionAsync(db, "case-a", 1, LifecycleState.Active);
        var v2 = await SeedVersionAsync(db, "case-b", 1, LifecycleState.Active);
        await SeedBindingAsync(db, v1.Id, BindingTargetType.Template, "template:abc", BindStrength.Mandatory);
        await SeedBindingAsync(db, v2.Id, BindingTargetType.Template, "template:Abc", BindStrength.Mandatory);

        var lower = await resolver.ResolveExactAsync(BindingTargetType.Template, "template:abc");
        var mixed = await resolver.ResolveExactAsync(BindingTargetType.Template, "template:Abc");

        lower.Bindings.Should().ContainSingle().Which.PolicyVersionId.Should().Be(v1.Id);
        mixed.Bindings.Should().ContainSingle().Which.PolicyVersionId.Should().Be(v2.Id);
    }

    [Fact]
    public async Task Resolve_FiltersOutSoftDeletedBindings()
    {
        var (resolver, db) = NewResolver();
        var v = await SeedVersionAsync(db, "del-pol", 1, LifecycleState.Active);
        var alive = await SeedBindingAsync(db, v.Id, BindingTargetType.Repo, "repo:a/live", BindStrength.Mandatory);
        var dead = await SeedBindingAsync(db, v.Id, BindingTargetType.Repo, "repo:a/live", BindStrength.Recommended);
        dead.DeletedAt = DateTimeOffset.UtcNow;
        dead.DeletedBySubjectId = "test";
        await db.SaveChangesAsync();

        var response = await resolver.ResolveExactAsync(BindingTargetType.Repo, "repo:a/live");

        response.Bindings.Should().ContainSingle().Which.BindingId.Should().Be(alive.Id);
    }

    [Fact]
    public async Task Resolve_OrdersByPolicyNameAsc_ThenVersionNumberDesc()
    {
        var (resolver, db) = NewResolver();
        var alphaV1 = await SeedVersionAsync(db, "alpha", 1, LifecycleState.Active);
        var alphaV2 = await SeedVersionAsync(db, "alpha", 2, LifecycleState.WindingDown);
        var betaV1 = await SeedVersionAsync(db, "beta", 1, LifecycleState.Active);
        const string target = "scope:abc";
        await SeedBindingAsync(db, alphaV1.Id, BindingTargetType.ScopeNode, target, BindStrength.Mandatory);
        await SeedBindingAsync(db, alphaV2.Id, BindingTargetType.ScopeNode, target, BindStrength.Mandatory);
        await SeedBindingAsync(db, betaV1.Id, BindingTargetType.ScopeNode, target, BindStrength.Mandatory);

        var response = await resolver.ResolveExactAsync(BindingTargetType.ScopeNode, target);

        response.Bindings.Select(b => (b.PolicyName, b.VersionNumber)).Should().ContainInOrder(
            ("alpha", 2),
            ("alpha", 1),
            ("beta", 1));
    }

    [Fact]
    public async Task Resolve_EmitsWireFormatCasing_ForEnforcementSeverityState()
    {
        var (resolver, db) = NewResolver();
        var policy = PolicyBuilders.APolicy(name: "wire-pol");
        var version = PolicyBuilders.AVersion(policy.Id, number: 1, state: LifecycleState.Active);
        version.Enforcement = EnforcementLevel.Must;
        version.Severity = Severity.Critical;
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        await SeedBindingAsync(db, version.Id, BindingTargetType.Template, "template:wire", BindStrength.Mandatory);

        var response = await resolver.ResolveExactAsync(BindingTargetType.Template, "template:wire");

        var dto = response.Bindings.Single();
        dto.Enforcement.Should().Be("MUST");
        dto.Severity.Should().Be("critical");
        dto.VersionState.Should().Be("Active");
    }
}
