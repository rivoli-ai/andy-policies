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
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// Consolidated tighten-only matrix (P4.7, story
/// rivoli-ai/andy-policies#36). Captures every cell of the
/// (ancestor strength × proposed strength) decision table from the
/// P4 epic body in a single place so a future reviewer can audit the
/// rule set at a glance instead of grepping across the per-story
/// validator tests in <see cref="TightenOnlyValidatorTests"/>. Each
/// row is a parameterised <see cref="TheoryAttribute"/> case that
/// asserts either <c>null</c> (allowed) or a populated
/// <see cref="TightenViolation"/> with the expected offending scope.
/// </summary>
public class TightenOnlyMatrixTests
{
    private static (TightenOnlyValidator validator, ScopeService scopes, AppDbContext db) NewServices()
    {
        var db = InMemoryDbFixture.Create();
        var scopes = new ScopeService(db, TimeProvider.System);
        var validator = new TightenOnlyValidator(db, scopes);
        return (validator, scopes, db);
    }

    private sealed record ChainIds(Guid Org, Guid Tenant, Guid Team, Guid Repo);

    private static async Task<ChainIds> SeedChainAsync(ScopeService scopes)
    {
        var org = await scopes.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:tov-mat", "Org"));
        var tenant = await scopes.CreateAsync(new CreateScopeNodeRequest(
            org.Id, ScopeType.Tenant, "tenant:tov-mat", "Tn"));
        var team = await scopes.CreateAsync(new CreateScopeNodeRequest(
            tenant.Id, ScopeType.Team, "team:tov-mat", "Tm"));
        var repo = await scopes.CreateAsync(new CreateScopeNodeRequest(
            team.Id, ScopeType.Repo, "repo:tov-mat/svc", "Repo"));
        return new ChainIds(org.Id, tenant.Id, team.Id, repo.Id);
    }

    private static async Task<PolicyVersion> SeedPolicyAsync(AppDbContext db, string name)
    {
        var policy = PolicyBuilders.APolicy(name: name);
        var version = PolicyBuilders.AVersion(policy.Id, number: 1, state: LifecycleState.Active);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return version;
    }

    private static async Task<Binding> AddBindingAsync(
        AppDbContext db, Guid scopeNodeId, Guid policyVersionId, BindStrength strength)
    {
        var binding = new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = policyVersionId,
            TargetType = BindingTargetType.ScopeNode,
            TargetRef = $"scope:{scopeNodeId}",
            BindStrength = strength,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "test",
        };
        db.Bindings.Add(binding);
        await db.SaveChangesAsync();
        return binding;
    }

    // -- The matrix ------------------------------------------------------

    // Row 1: no ancestor binding, leaf Recommended → allowed.
    [Fact]
    public async Task Row1_NoAncestor_LeafRecommended_Allowed()
    {
        var (v, s, _) = NewServices();
        var c = await SeedChainAsync(s);
        var version = await SeedPolicyAsync(((ScopeService)s).Db(), "row1");
        var result = await v.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode, $"scope:{c.Repo}", BindStrength.Recommended);
        result.Should().BeNull();
    }

    // Row 2: no ancestor binding, leaf Mandatory → allowed.
    [Fact]
    public async Task Row2_NoAncestor_LeafMandatory_Allowed()
    {
        var (v, s, _) = NewServices();
        var c = await SeedChainAsync(s);
        var version = await SeedPolicyAsync(((ScopeService)s).Db(), "row2");
        var result = await v.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode, $"scope:{c.Repo}", BindStrength.Mandatory);
        result.Should().BeNull();
    }

    // Row 3: ancestor Recommended, leaf Recommended → allowed.
    [Fact]
    public async Task Row3_AncestorRecommended_LeafRecommended_Allowed()
    {
        var (v, s, db) = NewServices();
        var c = await SeedChainAsync(s);
        var version = await SeedPolicyAsync(db, "row3");
        await AddBindingAsync(db, c.Org, version.Id, BindStrength.Recommended);
        var result = await v.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode, $"scope:{c.Repo}", BindStrength.Recommended);
        result.Should().BeNull();
    }

    // Row 4: ancestor Recommended, leaf Mandatory → allowed (upgrade).
    [Fact]
    public async Task Row4_AncestorRecommended_LeafMandatory_Allowed_Upgrade()
    {
        var (v, s, db) = NewServices();
        var c = await SeedChainAsync(s);
        var version = await SeedPolicyAsync(db, "row4");
        await AddBindingAsync(db, c.Org, version.Id, BindStrength.Recommended);
        var result = await v.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode, $"scope:{c.Repo}", BindStrength.Mandatory);
        result.Should().BeNull();
    }

    // Row 5: ancestor Mandatory, leaf Recommended → 409 violation.
    [Fact]
    public async Task Row5_AncestorMandatory_LeafRecommended_Rejected()
    {
        var (v, s, db) = NewServices();
        var c = await SeedChainAsync(s);
        var version = await SeedPolicyAsync(db, "row5");
        var ancestorBinding = await AddBindingAsync(db, c.Org, version.Id, BindStrength.Mandatory);

        var result = await v.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode, $"scope:{c.Repo}", BindStrength.Recommended);

        result.Should().NotBeNull();
        result!.OffendingAncestorBindingId.Should().Be(ancestorBinding.Id);
        result.OffendingScopeNodeId.Should().Be(c.Org);
        result.PolicyKey.Should().Be("row5");
    }

    // Row 6: ancestor Mandatory, leaf Mandatory → allowed (no-op at read).
    [Fact]
    public async Task Row6_AncestorMandatory_LeafMandatory_Allowed_DuplicateNotLoosening()
    {
        var (v, s, db) = NewServices();
        var c = await SeedChainAsync(s);
        var version = await SeedPolicyAsync(db, "row6");
        await AddBindingAsync(db, c.Org, version.Id, BindStrength.Mandatory);
        var result = await v.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode, $"scope:{c.Repo}", BindStrength.Mandatory);
        result.Should().BeNull();
    }

    // Row 7: Mandatory@Org + Recommended@Tenant; Recommended@Team →
    // violation. The deepest Mandatory wins as offending source.
    [Fact]
    public async Task Row7_MultipleAncestors_DeepestMandatoryReported()
    {
        var (v, s, db) = NewServices();
        var c = await SeedChainAsync(s);
        var version = await SeedPolicyAsync(db, "row7");
        var orgBinding = await AddBindingAsync(db, c.Org, version.Id, BindStrength.Mandatory);
        await AddBindingAsync(db, c.Tenant, version.Id, BindStrength.Recommended);

        var result = await v.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode, $"scope:{c.Team}", BindStrength.Recommended);

        result.Should().NotBeNull();
        result!.OffendingAncestorBindingId.Should().Be(orgBinding.Id);
        result.OffendingScopeNodeId.Should().Be(c.Org,
            "the only Mandatory ancestor in the chain wins as source");
    }

    // Row 8: Mandatory@Org + Mandatory@Team; Recommended@Repo → violation
    // points at Mandatory@Team (most specific Mandatory ancestor).
    [Fact]
    public async Task Row8_TwoMandatoryAncestors_MostSpecificWinsAsSource()
    {
        var (v, s, db) = NewServices();
        var c = await SeedChainAsync(s);
        var version = await SeedPolicyAsync(db, "row8");
        await AddBindingAsync(db, c.Org, version.Id, BindStrength.Mandatory);
        var teamBinding = await AddBindingAsync(db, c.Team, version.Id, BindStrength.Mandatory);

        var result = await v.ValidateCreateAsync(
            version.Id, BindingTargetType.ScopeNode, $"scope:{c.Repo}", BindStrength.Recommended);

        result.Should().NotBeNull();
        result!.OffendingAncestorBindingId.Should().Be(teamBinding.Id);
        result.OffendingScopeNodeId.Should().Be(c.Team);
    }

    // Row 9: ancestor Mandatory but a different PolicyId — never flags
    // the proposed write.
    [Fact]
    public async Task Row9_DifferentPolicyId_NeverCrossLeaks()
    {
        var (v, s, db) = NewServices();
        var c = await SeedChainAsync(s);
        var ancestor = await SeedPolicyAsync(db, "ancestor-pol");
        var proposed = await SeedPolicyAsync(db, "proposed-pol");
        await AddBindingAsync(db, c.Org, ancestor.Id, BindStrength.Mandatory);

        var result = await v.ValidateCreateAsync(
            proposed.Id, BindingTargetType.ScopeNode, $"scope:{c.Repo}", BindStrength.Recommended);

        result.Should().BeNull();
    }

    // Row 10: ancestor Mandatory but the proposed targetRef is a soft
    // ref (not a known scope) — skip the walk, allow the write per
    // the P3 non-goal.
    [Fact]
    public async Task Row10_SoftRef_NotResolvableToScopeNode_AllowsWrite()
    {
        var (v, s, db) = NewServices();
        var c = await SeedChainAsync(s);
        var version = await SeedPolicyAsync(db, "row10");
        await AddBindingAsync(db, c.Org, version.Id, BindStrength.Mandatory);

        var result = await v.ValidateCreateAsync(
            version.Id, BindingTargetType.Repo, "repo:never-heard-of/this", BindStrength.Recommended);

        result.Should().BeNull();
    }
}

/// <summary>
/// Test-only adapter to expose the <see cref="ScopeService"/>'s
/// underlying <c>AppDbContext</c> for fixtures that share an EF
/// instance across the service + raw seed inserts.
/// </summary>
internal static class ScopeServiceTestAccessors
{
    private static readonly System.Reflection.FieldInfo DbField =
        typeof(ScopeService).GetField("_db",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    public static AppDbContext Db(this ScopeService service) => (AppDbContext)DbField.GetValue(service)!;
}
