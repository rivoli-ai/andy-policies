// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="BindingResolutionService"/> (P4.3, story
/// rivoli-ai/andy-policies#30). Walks a hand-seeded scope chain over
/// EF Core InMemory and asserts the tighten-only fold rules from the
/// issue body verbatim:
/// <list type="bullet">
///   <item>Worked example: 3 entries with correct strengths + sources.</item>
///   <item>Ancestor Mandatory + descendant Recommended → Mandatory wins,
///     ancestor as source (descendant silently dropped).</item>
///   <item>Ancestor Recommended + descendant Mandatory → upgrade,
///     descendant as source.</item>
///   <item>Same-depth tie of two Mandatories on same PolicyId → older
///     CreatedAt wins.</item>
///   <item>Leaf-only binding returns exactly one entry.</item>
///   <item>Retired versions are NOT filtered (consumers decide).</item>
/// </list>
/// </summary>
public class BindingResolutionServiceTests
{
    private static (BindingResolutionService resolver, ScopeService scopes, AppDbContext db)
        NewServices()
    {
        var db = InMemoryDbFixture.Create();
        var scopes = new ScopeService(db, TimeProvider.System);
        var resolver = new BindingResolutionService(db, scopes);
        return (resolver, scopes, db);
    }

    private sealed record ChainIds(Guid Org, Guid Tenant, Guid Team, Guid Repo, Guid Template);

    private static async Task<ChainIds> SeedFiveLevelChainAsync(ScopeService scopes)
    {
        var org = await scopes.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:acme", "Acme"));
        var tenant = await scopes.CreateAsync(new CreateScopeNodeRequest(
            org.Id, ScopeType.Tenant, "tenant:t1", "T1"));
        var team = await scopes.CreateAsync(new CreateScopeNodeRequest(
            tenant.Id, ScopeType.Team, "team:red", "Red"));
        var repo = await scopes.CreateAsync(new CreateScopeNodeRequest(
            team.Id, ScopeType.Repo, "repo:acme/svc", "Service"));
        var template = await scopes.CreateAsync(new CreateScopeNodeRequest(
            repo.Id, ScopeType.Template, "template:deploy", "Deploy"));
        return new ChainIds(org.Id, tenant.Id, team.Id, repo.Id, template.Id);
    }

    private static async Task<(Policy policy, PolicyVersion version)> SeedPolicyAndPublishAsync(
        AppDbContext db, string name, int versionNumber = 1)
    {
        var policy = PolicyBuilders.APolicy(name: name);
        var version = PolicyBuilders.AVersion(policy.Id, number: versionNumber, state: LifecycleState.Active);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return (policy, version);
    }

    private static async Task<Binding> AddScopeBindingAsync(
        AppDbContext db, Guid scopeNodeId, Guid policyVersionId,
        BindStrength strength, DateTimeOffset? createdAt = null)
    {
        var binding = new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = policyVersionId,
            TargetType = BindingTargetType.ScopeNode,
            TargetRef = $"scope:{scopeNodeId}",
            BindStrength = strength,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            CreatedBySubjectId = "test",
        };
        db.Bindings.Add(binding);
        await db.SaveChangesAsync();
        return binding;
    }

    [Fact]
    public async Task WorkedExample_FromIssueBody_ProducesThreeEntriesWithCorrectStrengthsAndSources()
    {
        var (resolver, scopes, db) = NewServices();
        var chain = await SeedFiveLevelChainAsync(scopes);
        var (_, noProd) = await SeedPolicyAndPublishAsync(db, "no-prod");
        var (_, sandboxed) = await SeedPolicyAndPublishAsync(db, "sandboxed");
        var (_, highRisk) = await SeedPolicyAndPublishAsync(db, "high-risk");

        await AddScopeBindingAsync(db, chain.Org, noProd.Id, BindStrength.Mandatory);
        await AddScopeBindingAsync(db, chain.Team, sandboxed.Id, BindStrength.Recommended);
        await AddScopeBindingAsync(db, chain.Repo, highRisk.Id, BindStrength.Mandatory);

        var result = await resolver.ResolveForScopeAsync(chain.Repo);

        result.ScopeNodeId.Should().Be(chain.Repo);
        result.Policies.Should().HaveCount(3);

        // Mandatories come first, then Recommended, alpha-by-key within strength.
        result.Policies[0].PolicyKey.Should().Be("high-risk");
        result.Policies[0].BindStrength.Should().Be(BindStrength.Mandatory);
        result.Policies[0].SourceScopeNodeId.Should().Be(chain.Repo);

        result.Policies[1].PolicyKey.Should().Be("no-prod");
        result.Policies[1].BindStrength.Should().Be(BindStrength.Mandatory);
        result.Policies[1].SourceScopeNodeId.Should().Be(chain.Org);

        result.Policies[2].PolicyKey.Should().Be("sandboxed");
        result.Policies[2].BindStrength.Should().Be(BindStrength.Recommended);
        result.Policies[2].SourceScopeNodeId.Should().Be(chain.Team);
    }

    [Fact]
    public async Task AncestorMandatory_PlusDescendantRecommended_KeepsMandatoryWithAncestorSource()
    {
        var (resolver, scopes, db) = NewServices();
        var chain = await SeedFiveLevelChainAsync(scopes);
        var (_, noProd) = await SeedPolicyAndPublishAsync(db, "no-prod");
        await AddScopeBindingAsync(db, chain.Org, noProd.Id, BindStrength.Mandatory);
        await AddScopeBindingAsync(db, chain.Repo, noProd.Id, BindStrength.Recommended);

        var result = await resolver.ResolveForScopeAsync(chain.Repo);

        result.Policies.Should().ContainSingle();
        result.Policies[0].BindStrength.Should().Be(BindStrength.Mandatory);
        result.Policies[0].SourceScopeNodeId.Should().Be(chain.Org);
    }

    [Fact]
    public async Task AncestorRecommended_PlusDescendantMandatory_UpgradesToMandatoryWithDescendantSource()
    {
        var (resolver, scopes, db) = NewServices();
        var chain = await SeedFiveLevelChainAsync(scopes);
        var (_, sandboxed) = await SeedPolicyAndPublishAsync(db, "sandboxed");
        await AddScopeBindingAsync(db, chain.Team, sandboxed.Id, BindStrength.Recommended);
        await AddScopeBindingAsync(db, chain.Repo, sandboxed.Id, BindStrength.Mandatory);

        var result = await resolver.ResolveForScopeAsync(chain.Repo);

        result.Policies.Should().ContainSingle();
        result.Policies[0].BindStrength.Should().Be(BindStrength.Mandatory);
        result.Policies[0].SourceScopeNodeId.Should().Be(chain.Repo);
    }

    [Fact]
    public async Task SameDepthTie_TwoMandatoriesOnSamePolicy_OlderCreatedAtWins()
    {
        var (resolver, scopes, db) = NewServices();
        var chain = await SeedFiveLevelChainAsync(scopes);
        var (_, sandboxed) = await SeedPolicyAndPublishAsync(db, "sandboxed");

        var older = await AddScopeBindingAsync(
            db, chain.Repo, sandboxed.Id, BindStrength.Mandatory,
            DateTimeOffset.UtcNow.AddMinutes(-10));
        await AddScopeBindingAsync(
            db, chain.Repo, sandboxed.Id, BindStrength.Mandatory,
            DateTimeOffset.UtcNow);

        var result = await resolver.ResolveForScopeAsync(chain.Repo);

        result.Policies.Should().ContainSingle();
        result.Policies[0].SourceBindingId.Should().Be(older.Id);
    }

    [Fact]
    public async Task LeafOnlyBinding_ReturnsExactlyOneEntry()
    {
        var (resolver, scopes, db) = NewServices();
        var chain = await SeedFiveLevelChainAsync(scopes);
        var (_, version) = await SeedPolicyAndPublishAsync(db, "leaf-only");
        await AddScopeBindingAsync(db, chain.Repo, version.Id, BindStrength.Mandatory);

        var result = await resolver.ResolveForScopeAsync(chain.Repo);

        result.Policies.Should().ContainSingle();
        result.Policies[0].PolicyKey.Should().Be("leaf-only");
        result.Policies[0].SourceScopeNodeId.Should().Be(chain.Repo);
    }

    [Fact]
    public async Task RetiredVersion_StillReturnedByResolution_ConsumersHandleDeprecation()
    {
        var (resolver, scopes, db) = NewServices();
        var chain = await SeedFiveLevelChainAsync(scopes);
        var policy = PolicyBuilders.APolicy(name: "retired-policy");
        var version = PolicyBuilders.AVersion(policy.Id, number: 1, state: LifecycleState.Retired);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        await AddScopeBindingAsync(db, chain.Repo, version.Id, BindStrength.Mandatory);

        var result = await resolver.ResolveForScopeAsync(chain.Repo);

        result.Policies.Should().ContainSingle();
        result.Policies[0].PolicyKey.Should().Be("retired-policy");
    }

    [Fact]
    public async Task SoftDeletedBinding_IsExcludedFromResolution()
    {
        var (resolver, scopes, db) = NewServices();
        var chain = await SeedFiveLevelChainAsync(scopes);
        var (_, version) = await SeedPolicyAndPublishAsync(db, "tombstoned");
        var binding = await AddScopeBindingAsync(db, chain.Repo, version.Id, BindStrength.Mandatory);
        binding.DeletedAt = DateTimeOffset.UtcNow;
        binding.DeletedBySubjectId = "test";
        await db.SaveChangesAsync();

        var result = await resolver.ResolveForScopeAsync(chain.Repo);

        result.Policies.Should().BeEmpty();
    }

    [Fact]
    public async Task BridgeBinding_OnRepoExternalRef_PicksUpInChainWalk()
    {
        // A binding authored against TargetType=Repo, TargetRef="repo:acme/svc"
        // (a P3 binding pre-dating scope nodes) should be picked up by the
        // chain walker when the leaf scope's Ref matches.
        var (resolver, scopes, db) = NewServices();
        var chain = await SeedFiveLevelChainAsync(scopes);
        var (_, version) = await SeedPolicyAndPublishAsync(db, "bridge-policy");
        var binding = new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = version.Id,
            TargetType = BindingTargetType.Repo,
            TargetRef = "repo:acme/svc",
            BindStrength = BindStrength.Mandatory,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "test",
        };
        db.Bindings.Add(binding);
        await db.SaveChangesAsync();

        var result = await resolver.ResolveForScopeAsync(chain.Repo);

        result.Policies.Should().ContainSingle();
        result.Policies[0].PolicyKey.Should().Be("bridge-policy");
        result.Policies[0].SourceBindingId.Should().Be(binding.Id);
        result.Policies[0].SourceScopeNodeId.Should().Be(chain.Repo);
    }

    [Fact]
    public async Task ResolveForScopeAsync_OnUnknownId_ThrowsNotFound()
    {
        var (resolver, _, _) = NewServices();

        var act = async () => await resolver.ResolveForScopeAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ResolveForTargetAsync_OnKnownScopeRef_WalksTheChain()
    {
        var (resolver, scopes, db) = NewServices();
        var chain = await SeedFiveLevelChainAsync(scopes);
        var (_, version) = await SeedPolicyAndPublishAsync(db, "via-target");
        await AddScopeBindingAsync(db, chain.Org, version.Id, BindStrength.Mandatory);

        var result = await resolver.ResolveForTargetAsync(BindingTargetType.Repo, "repo:acme/svc");

        result.ScopeNodeId.Should().Be(chain.Repo);
        result.Policies.Should().ContainSingle();
        result.Policies[0].SourceScopeNodeId.Should().Be(chain.Org);
    }

    [Fact]
    public async Task ResolveForTargetAsync_OnUnknownScope_DegradesToExactMatch()
    {
        var (resolver, _, db) = NewServices();
        var (_, version) = await SeedPolicyAndPublishAsync(db, "exact-only");
        var binding = new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = version.Id,
            TargetType = BindingTargetType.Repo,
            TargetRef = "repo:unknown/repo",
            BindStrength = BindStrength.Mandatory,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "test",
        };
        db.Bindings.Add(binding);
        await db.SaveChangesAsync();

        var result = await resolver.ResolveForTargetAsync(BindingTargetType.Repo, "repo:unknown/repo");

        result.ScopeNodeId.Should().BeNull("no scope node matched — fallback to exact-match");
        result.Policies.Should().ContainSingle();
        result.Policies[0].PolicyKey.Should().Be("exact-only");
    }
}
