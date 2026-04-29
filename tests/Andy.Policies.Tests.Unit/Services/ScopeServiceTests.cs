// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="ScopeService"/> (P4.2, story
/// rivoli-ai/andy-policies#29). Drives the service over EF Core
/// InMemory to lock down: type-ladder enforcement, materialized-path
/// math, walk-up / walk-down ordering, tree assembly, delete-with-
/// children rejection, and the unique <c>(Type, Ref)</c> path.
/// </summary>
public class ScopeServiceTests
{
    private static (ScopeService service, AppDbContext db) NewService()
    {
        var db = InMemoryDbFixture.Create();
        var service = new ScopeService(db, TimeProvider.System);
        return (service, db);
    }

    private static async Task<(Guid org, Guid tenant, Guid team, Guid repo, Guid template, Guid run)>
        SeedSixLevelChainAsync(ScopeService svc)
    {
        var org = await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:acme", "Acme Org"));
        var tenant = await svc.CreateAsync(new CreateScopeNodeRequest(
            org.Id, ScopeType.Tenant, "tenant:t1", "Tenant 1"));
        var team = await svc.CreateAsync(new CreateScopeNodeRequest(
            tenant.Id, ScopeType.Team, "team:red", "Team Red"));
        var repo = await svc.CreateAsync(new CreateScopeNodeRequest(
            team.Id, ScopeType.Repo, "repo:acme/svc", "Service Repo"));
        var template = await svc.CreateAsync(new CreateScopeNodeRequest(
            repo.Id, ScopeType.Template, "template:deploy", "Deploy Template"));
        var run = await svc.CreateAsync(new CreateScopeNodeRequest(
            template.Id, ScopeType.Run, "run:1", "Run 1"));
        return (org.Id, tenant.Id, team.Id, repo.Id, template.Id, run.Id);
    }

    [Fact]
    public async Task CreateAsync_RootOrg_StampsDepthZero_AndPathIsSlashSelfId()
    {
        var (svc, _) = NewService();

        var dto = await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:root-test", "Root"));

        dto.ParentId.Should().BeNull();
        dto.Type.Should().Be(ScopeType.Org);
        dto.Depth.Should().Be(0);
        dto.MaterializedPath.Should().Be($"/{dto.Id}");
    }

    [Fact]
    public async Task CreateAsync_RootMustBeOrg_OtherTypesAreRejected()
    {
        var (svc, _) = NewService();

        var act = async () => await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Tenant, "tenant:bad-root", "Tenant"));

        await act.Should().ThrowAsync<InvalidScopeTypeException>();
    }

    [Fact]
    public async Task CreateAsync_TeamUnderOrg_FailsWithLadderError()
    {
        // Team's parent must be Tenant, not Org — the canonical ladder
        // is Org → Tenant → Team → Repo → Template → Run.
        var (svc, _) = NewService();
        var org = await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:lo", "Org"));

        var act = async () => await svc.CreateAsync(new CreateScopeNodeRequest(
            org.Id, ScopeType.Team, "team:wrong", "Team"));

        await act.Should().ThrowAsync<InvalidScopeTypeException>()
            .WithMessage("*Team*Org*");
    }

    [Fact]
    public async Task CreateAsync_TeamUnderTenant_Succeeds_AndPathExtendsParent()
    {
        var (svc, _) = NewService();
        var org = await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:p", "Org"));
        var tenant = await svc.CreateAsync(new CreateScopeNodeRequest(
            org.Id, ScopeType.Tenant, "tenant:t", "T"));

        var team = await svc.CreateAsync(new CreateScopeNodeRequest(
            tenant.Id, ScopeType.Team, "team:t", "Team"));

        team.Depth.Should().Be(2);
        team.MaterializedPath.Should().Be($"/{org.Id}/{tenant.Id}/{team.Id}");
    }

    // CreateAsync duplicate (Type, Ref) detection is exercised by
    // ScopeServiceConcurrencyTests against a real Postgres testcontainer
    // (see #29 acceptance). EF InMemory does not honour unique
    // indexes, so a unit-level assertion would silently pass even if the
    // service layer dropped the conflict-detection path.

    [Fact]
    public async Task CreateAsync_WithMissingParent_ThrowsNotFound()
    {
        var (svc, _) = NewService();

        var act = async () => await svc.CreateAsync(new CreateScopeNodeRequest(
            Guid.NewGuid(), ScopeType.Tenant, "tenant:orphan", "Orphan"));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public async Task CreateAsync_WithEmptyOrWhitespaceRef_ThrowsValidation(string @ref)
    {
        var (svc, _) = NewService();

        var act = async () => await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, @ref, "Display"));

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateAsync_WithOversizedRef_ThrowsValidation()
    {
        var (svc, _) = NewService();
        var oversized = new string('a', 513);

        var act = async () => await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, oversized, "Display"));

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*512*");
    }

    [Fact]
    public async Task UpdateAsync_OnExistingNode_UpdatesDisplayName_AndStampsUpdatedAt()
    {
        var (svc, _) = NewService();
        var dto = await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:upd", "Old Name"));

        await Task.Delay(5);
        var updated = await svc.UpdateAsync(dto.Id, new UpdateScopeNodeRequest("New Name"));

        updated.DisplayName.Should().Be("New Name");
        updated.UpdatedAt.Should().BeOnOrAfter(dto.CreatedAt);
    }

    [Fact]
    public async Task UpdateAsync_OnUnknownId_ThrowsNotFound()
    {
        var (svc, _) = NewService();

        var act = async () => await svc.UpdateAsync(
            Guid.NewGuid(), new UpdateScopeNodeRequest("Ignored"));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_OnLeaf_RemovesRow()
    {
        var (svc, db) = NewService();
        var dto = await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:del-leaf", "Del"));

        await svc.DeleteAsync(dto.Id);

        var exists = await db.ScopeNodes.AnyAsync(s => s.Id == dto.Id);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_OnNodeWithChildren_ThrowsScopeHasDescendants()
    {
        var (svc, _) = NewService();
        var (org, tenant, _, _, _, _) = await SeedSixLevelChainAsync(svc);

        var act = async () => await svc.DeleteAsync(org);

        var thrown = await act.Should().ThrowAsync<ScopeHasDescendantsException>();
        thrown.Which.ScopeNodeId.Should().Be(org);
        thrown.Which.ChildCount.Should().Be(1);
    }

    [Fact]
    public async Task DeleteAsync_OnUnknownId_ThrowsNotFound()
    {
        var (svc, _) = NewService();

        var act = async () => await svc.DeleteAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetAncestorsAsync_OnDeepLeaf_ReturnsFiveAncestors_RootFirst()
    {
        var (svc, _) = NewService();
        var (org, tenant, team, repo, template, run) = await SeedSixLevelChainAsync(svc);

        var ancestors = await svc.GetAncestorsAsync(run);

        ancestors.Select(a => a.Id).Should().ContainInOrder(org, tenant, team, repo, template);
        ancestors.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetAncestorsAsync_OnRoot_ReturnsEmptyList()
    {
        var (svc, _) = NewService();
        var dto = await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:lonely", "Solo"));

        var ancestors = await svc.GetAncestorsAsync(dto.Id);

        ancestors.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAncestorsAsync_OnUnknownId_ThrowsNotFound()
    {
        var (svc, _) = NewService();

        var act = async () => await svc.GetAncestorsAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetDescendantsAsync_OnRoot_ReturnsEverythingExceptSelf()
    {
        var (svc, _) = NewService();
        var (org, tenant, team, repo, template, run) = await SeedSixLevelChainAsync(svc);

        var descendants = await svc.GetDescendantsAsync(org);

        descendants.Should().HaveCount(5);
        descendants.Select(d => d.Id).Should().NotContain(org);
        descendants.Select(d => d.Id).Should().Contain(new[] { tenant, team, repo, template, run });
    }

    [Fact]
    public async Task GetDescendantsAsync_OnLeaf_ReturnsEmptyList()
    {
        var (svc, _) = NewService();
        var (_, _, _, _, _, run) = await SeedSixLevelChainAsync(svc);

        var descendants = await svc.GetDescendantsAsync(run);

        descendants.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTreeAsync_OnEmptyDb_ReturnsEmptyForest()
    {
        var (svc, _) = NewService();

        var tree = await svc.GetTreeAsync();

        tree.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTreeAsync_BuildsNestedShape_FromSixLevelChain()
    {
        var (svc, _) = NewService();
        var (org, _, _, _, _, _) = await SeedSixLevelChainAsync(svc);

        var forest = await svc.GetTreeAsync();

        forest.Should().ContainSingle();
        var orgNode = forest.Single();
        orgNode.Node.Id.Should().Be(org);
        orgNode.Children.Should().ContainSingle();
        // Walk a few levels to confirm the shape.
        var tenantNode = orgNode.Children.Single();
        tenantNode.Children.Should().ContainSingle();
        var teamNode = tenantNode.Children.Single();
        teamNode.Children.Should().ContainSingle();
    }

    [Fact]
    public async Task GetByRefAsync_ReturnsExactMatch_OrNull()
    {
        var (svc, _) = NewService();
        await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:lookup", "Lookup"));

        var hit = await svc.GetByRefAsync(ScopeType.Org, "org:lookup");
        var miss = await svc.GetByRefAsync(ScopeType.Org, "org:missing");

        hit.Should().NotBeNull();
        hit!.Ref.Should().Be("org:lookup");
        miss.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_FiltersByType_OrReturnsAllSorted()
    {
        var (svc, _) = NewService();
        var (org, tenant, team, _, _, _) = await SeedSixLevelChainAsync(svc);

        var all = await svc.ListAsync(type: null);
        var tenants = await svc.ListAsync(type: ScopeType.Tenant);

        all.Should().HaveCount(6);
        all.First().Id.Should().Be(org); // depth 0 first
        tenants.Should().ContainSingle().Which.Id.Should().Be(tenant);
    }
}
