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
/// Unit tests for <see cref="TightenOnlyValidator"/> (P4.4, story
/// rivoli-ai/andy-policies#32). Drives the validator over EF Core
/// InMemory across the full ancestor × proposed strength matrix and
/// the multi-ancestor / soft-ref edge cases.
/// </summary>
public class TightenOnlyValidatorTests
{
    private static (TightenOnlyValidator validator, ScopeService scopes, AppDbContext db)
        NewServices()
    {
        var db = InMemoryDbFixture.Create();
        var scopes = new ScopeService(db, TimeProvider.System);
        var validator = new TightenOnlyValidator(db, scopes);
        return (validator, scopes, db);
    }

    private sealed record ChainIds(Guid Org, Guid Tenant, Guid Team, Guid Repo);

    private static async Task<ChainIds> SeedFourLevelChainAsync(ScopeService scopes)
    {
        var org = await scopes.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:tov", "Org"));
        var tenant = await scopes.CreateAsync(new CreateScopeNodeRequest(
            org.Id, ScopeType.Tenant, "tenant:tov", "Tenant"));
        var team = await scopes.CreateAsync(new CreateScopeNodeRequest(
            tenant.Id, ScopeType.Team, "team:tov", "Team"));
        var repo = await scopes.CreateAsync(new CreateScopeNodeRequest(
            team.Id, ScopeType.Repo, "repo:tov/svc", "Repo"));
        return new ChainIds(org.Id, tenant.Id, team.Id, repo.Id);
    }

    private static async Task<(Policy policy, PolicyVersion version)> SeedPolicyAsync(
        AppDbContext db, string name)
    {
        var policy = PolicyBuilders.APolicy(name: name);
        var version = PolicyBuilders.AVersion(policy.Id, number: 1, state: LifecycleState.Active);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return (policy, version);
    }

    private static async Task AddAncestorBindingAsync(
        AppDbContext db, Guid scopeNodeId, Guid policyVersionId, BindStrength strength)
    {
        db.Bindings.Add(new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = policyVersionId,
            TargetType = BindingTargetType.ScopeNode,
            TargetRef = $"scope:{scopeNodeId}",
            BindStrength = strength,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "test",
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    // ancestor × proposed matrix. Only Mandatory × Recommended is a violation.
    [InlineData(null, BindStrength.Recommended, false)]
    [InlineData(null, BindStrength.Mandatory, false)]
    [InlineData(BindStrength.Recommended, BindStrength.Recommended, false)]
    [InlineData(BindStrength.Recommended, BindStrength.Mandatory, false)]
    [InlineData(BindStrength.Mandatory, BindStrength.Mandatory, false)]
    [InlineData(BindStrength.Mandatory, BindStrength.Recommended, true)]
    public async Task ValidateCreate_AncestorXProposedMatrix(
        BindStrength? ancestorStrength,
        BindStrength proposedStrength,
        bool expectViolation)
    {
        var (validator, scopes, db) = NewServices();
        var chain = await SeedFourLevelChainAsync(scopes);
        var (_, version) = await SeedPolicyAsync(db, "matrix-pol");
        if (ancestorStrength is { } strength)
        {
            await AddAncestorBindingAsync(db, chain.Org, version.Id, strength);
        }

        var result = await validator.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode, $"scope:{chain.Repo}", proposedStrength);

        if (expectViolation)
        {
            result.Should().NotBeNull();
            result!.OffendingScopeNodeId.Should().Be(chain.Org);
            result.PolicyKey.Should().Be("matrix-pol");
        }
        else
        {
            result.Should().BeNull();
        }
    }

    [Fact]
    public async Task ValidateCreate_MultipleAncestors_ReportsDeepestMandatory()
    {
        // Root Recommended + parent Mandatory; proposed leaf Recommended →
        // violation reports the parent (deeper-is-more-specific).
        var (validator, scopes, db) = NewServices();
        var chain = await SeedFourLevelChainAsync(scopes);
        var (_, version) = await SeedPolicyAsync(db, "multi-pol");
        await AddAncestorBindingAsync(db, chain.Org, version.Id, BindStrength.Recommended);
        await AddAncestorBindingAsync(db, chain.Team, version.Id, BindStrength.Mandatory);

        var result = await validator.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode, $"scope:{chain.Repo}", BindStrength.Recommended);

        result.Should().NotBeNull();
        result!.OffendingScopeNodeId.Should().Be(chain.Team,
            "the deeper Mandatory ancestor is more specific");
    }

    [Fact]
    public async Task ValidateCreate_UnrelatedPolicyAtAncestor_NeverFlags()
    {
        var (validator, scopes, db) = NewServices();
        var chain = await SeedFourLevelChainAsync(scopes);
        var (_, ancestorVersion) = await SeedPolicyAsync(db, "ancestor-pol");
        var (_, proposedVersion) = await SeedPolicyAsync(db, "different-pol");
        await AddAncestorBindingAsync(db, chain.Org, ancestorVersion.Id, BindStrength.Mandatory);

        var result = await validator.ValidateCreateAsync(
            proposedVersion.Id, BindingTargetType.ScopeNode,
            $"scope:{chain.Repo}", BindStrength.Recommended);

        result.Should().BeNull("ancestor binds a different PolicyId");
    }

    [Fact]
    public async Task ValidateCreate_SoftRef_NotResolvableToScopeNode_AllowsCreate()
    {
        var (validator, _, db) = NewServices();
        var (_, version) = await SeedPolicyAsync(db, "soft-pol");

        // No scope nodes seeded with this Ref — soft-ref path skips the walk.
        var result = await validator.ValidateCreateAsync(
            version.Id, BindingTargetType.Repo, "repo:nowhere/never", BindStrength.Recommended);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateCreate_BridgeBinding_ViaAncestorRepoRef_FiresViolation()
    {
        // A binding authored against the ancestor's external Ref
        // (TargetType=Repo, TargetRef=repo:foo) should trip the
        // tighten check the same as a ScopeNode-targeted Mandatory.
        var (validator, scopes, db) = NewServices();
        var chain = await SeedFourLevelChainAsync(scopes);
        var (_, version) = await SeedPolicyAsync(db, "bridge-pol");

        // Anchor the Repo ancestor's Ref so the chain walk picks it up.
        var repoNode = await db.ScopeNodes.FirstAsync(s => s.Id == chain.Repo);
        db.Bindings.Add(new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = version.Id,
            TargetType = BindingTargetType.Repo,
            TargetRef = repoNode.Ref,
            BindStrength = BindStrength.Mandatory,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "test",
        });
        await db.SaveChangesAsync();

        // Add a Template scope under the Repo and propose a Recommended
        // binding there — should be rejected by the Repo-bridge Mandatory.
        var templateScope = await scopes.CreateAsync(new CreateScopeNodeRequest(
            chain.Repo, ScopeType.Template, "template:tov", "Template"));

        var result = await validator.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode,
            $"scope:{templateScope.Id}", BindStrength.Recommended);

        result.Should().NotBeNull();
        result!.OffendingScopeNodeId.Should().Be(chain.Repo);
    }

    [Fact]
    public async Task ValidateDelete_AlwaysReturnsNull_PerSpec()
    {
        var (validator, _, _) = NewServices();

        var result = await validator.ValidateDeleteAsync(Guid.NewGuid());

        result.Should().BeNull(
            "tighten-only is a CREATE-time invariant; deletes never violate the rule");
    }

    [Fact]
    public async Task ValidateCreate_MandatoryProposal_NeverFlags_EvenWithMandatoryAncestor()
    {
        // Adding a Mandatory under a Mandatory is a duplicate but not a
        // loosening — allowed. Reads will then dedup deepest-wins (P4.3).
        var (validator, scopes, db) = NewServices();
        var chain = await SeedFourLevelChainAsync(scopes);
        var (_, version) = await SeedPolicyAsync(db, "duplicate-pol");
        await AddAncestorBindingAsync(db, chain.Org, version.Id, BindStrength.Mandatory);

        var result = await validator.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode, $"scope:{chain.Repo}", BindStrength.Mandatory);

        result.Should().BeNull();
    }
}
