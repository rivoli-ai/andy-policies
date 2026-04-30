// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// Cycle-rejection coverage for the scope hierarchy (P4.7, story
/// rivoli-ai/andy-policies#36). The P4.1 design pinned cycle
/// prevention as <i>structural</i>: a node's <c>ParentId</c> is set
/// on creation and never mutated by the service contract, so cycles
/// are impossible by construction. These tests enforce the contract
/// — they're regression tests against any future change that would
/// add a re-parent path.
/// </summary>
public class ScopeCycleRejectionTests
{
    private static (ScopeService scopes, AppDbContext db) NewServices()
    {
        var db = InMemoryDbFixture.Create();
        return (new ScopeService(db, TimeProvider.System), db);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotMutateParentId_ByDesign()
    {
        // The UpdateScopeNodeRequest record exposes only DisplayName; the
        // service's UpdateAsync overload doesn't read ParentId. This test
        // documents the absence — a future record-shape extension that
        // adds ParentId would make this test fail at the call site,
        // forcing the author to evaluate the cycle implications.
        var (scopes, _) = NewServices();
        var org = await scopes.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:no-mutate", "Org"));
        var tenant = await scopes.CreateAsync(new CreateScopeNodeRequest(
            org.Id, ScopeType.Tenant, "tenant:no-mutate", "Tenant"));

        // The only update op available — DisplayName change. ParentId
        // remains the original org.
        var updated = await scopes.UpdateAsync(tenant.Id, new UpdateScopeNodeRequest("Renamed"));

        updated.ParentId.Should().Be(org.Id);
        updated.DisplayName.Should().Be("Renamed");
    }

    [Fact]
    public async Task CreateAsync_RequiresExistingParent_PreventingForwardLinkCycle()
    {
        // Cycles need at minimum two nodes A → B → A. Any new node
        // requires its parent already exists, so a child cannot be
        // ancestor-of-self at creation time.
        var (scopes, _) = NewServices();

        var act = async () => await scopes.CreateAsync(new CreateScopeNodeRequest(
            ParentId: Guid.NewGuid(),  // never seeded
            Type: ScopeType.Tenant,
            Ref: "tenant:cycle-attempt",
            DisplayName: "Cycle"));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task DirectDbCycle_ReportableButPreventedByPathInvariant()
    {
        // Suppose someone bypasses the service and writes a self-loop
        // directly to the DB. The service's GetAncestorsAsync parses
        // the materialized path and discards self-references; an
        // attacker cannot achieve infinite recursion through the read
        // path. This test simulates a corrupt row and asserts the
        // ancestor walk terminates without a stack overflow.
        var (scopes, db) = NewServices();
        var org = await scopes.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:corrupt", "Org"));

        // Manually corrupt the row: point ParentId at itself. The
        // materialized path still shows /{org.Id} though — the path
        // is what GetAncestorsAsync walks.
        var entity = await db.ScopeNodes.FirstAsync(s => s.Id == org.Id);
        entity.ParentId = org.Id;
        await db.SaveChangesAsync();

        var ancestors = await scopes.GetAncestorsAsync(org.Id);

        ancestors.Should().BeEmpty(
            "self-ids in the materialized path are filtered out, so a self-loop produces an empty ancestor list");
    }

    [Fact]
    public async Task LinearChain_AcceptedAsTheValidShape()
    {
        // Sanity check: the canonical valid hierarchy succeeds end-to-end.
        var (scopes, _) = NewServices();
        var org = await scopes.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:linear", "Org"));
        var tenant = await scopes.CreateAsync(new CreateScopeNodeRequest(
            org.Id, ScopeType.Tenant, "tenant:linear", "Tn"));
        var team = await scopes.CreateAsync(new CreateScopeNodeRequest(
            tenant.Id, ScopeType.Team, "team:linear", "Tm"));

        var ancestorsOfTeam = await scopes.GetAncestorsAsync(team.Id);

        ancestorsOfTeam.Select(a => a.Id).Should().ContainInOrder(org.Id, tenant.Id);
    }
}
