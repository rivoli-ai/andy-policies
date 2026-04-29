// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// Verifies that <see cref="BindingService"/> consults the injected
/// <see cref="ITightenOnlyValidator"/> on create and rolls back the
/// proposed insert when the validator returns a violation (P4.4,
/// story rivoli-ai/andy-policies#32). DeleteAsync never raises a
/// tighten-only error per the issue's reviewer-flagged
/// reconciliation.
/// </summary>
public class BindingServiceTightenOnlyTests
{
    private static (BindingService binding, ScopeService scopes, TightenOnlyValidator validator, AppDbContext db)
        NewServices()
    {
        var db = InMemoryDbFixture.Create();
        var scopes = new ScopeService(db, TimeProvider.System);
        var validator = new TightenOnlyValidator(db, scopes);
        var audit = new NoopAuditWriter(NullLogger<NoopAuditWriter>.Instance);
        var binding = new BindingService(db, audit, TimeProvider.System, validator);
        return (binding, scopes, validator, db);
    }

    private sealed record ChainIds(Guid Org, Guid Repo);

    private static async Task<ChainIds> SeedChainAsync(ScopeService scopes)
    {
        var org = await scopes.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:bs-tov", "Org"));
        var tenant = await scopes.CreateAsync(new CreateScopeNodeRequest(
            org.Id, ScopeType.Tenant, "tenant:bs-tov", "Tenant"));
        var team = await scopes.CreateAsync(new CreateScopeNodeRequest(
            tenant.Id, ScopeType.Team, "team:bs-tov", "Team"));
        var repo = await scopes.CreateAsync(new CreateScopeNodeRequest(
            team.Id, ScopeType.Repo, "repo:bs-tov/svc", "Repo"));
        return new ChainIds(org.Id, repo.Id);
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

    private static async Task AddBindingAsync(
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

    [Fact]
    public async Task CreateAsync_OnLoosening_Throws_AndDoesNotPersistRow()
    {
        var (svc, scopes, _, db) = NewServices();
        var chain = await SeedChainAsync(scopes);
        var (_, version) = await SeedPolicyAsync(db, "tov-pol");
        await AddBindingAsync(db, chain.Org, version.Id, BindStrength.Mandatory);
        var bindingCountBefore = await db.Bindings.CountAsync();

        var act = async () => await svc.CreateAsync(
            new CreateBindingRequest(
                version.Id,
                BindingTargetType.ScopeNode,
                $"scope:{chain.Repo}",
                BindStrength.Recommended),
            "test-actor");

        var thrown = await act.Should().ThrowAsync<TightenOnlyViolationException>();
        thrown.Which.Violation.OffendingScopeNodeId.Should().Be(chain.Org);
        thrown.Which.Violation.PolicyKey.Should().Be("tov-pol");

        var bindingCountAfter = await db.Bindings.CountAsync();
        bindingCountAfter.Should().Be(bindingCountBefore,
            "the violation must roll back the proposed insert");
    }

    [Fact]
    public async Task CreateAsync_TighteningProposal_Succeeds()
    {
        // Recommended ancestor + Mandatory descendant proposal: an
        // upgrade is tightening, not loosening — allowed.
        var (svc, scopes, _, db) = NewServices();
        var chain = await SeedChainAsync(scopes);
        var (_, version) = await SeedPolicyAsync(db, "upgrade-pol");
        await AddBindingAsync(db, chain.Org, version.Id, BindStrength.Recommended);

        var dto = await svc.CreateAsync(
            new CreateBindingRequest(
                version.Id,
                BindingTargetType.ScopeNode,
                $"scope:{chain.Repo}",
                BindStrength.Mandatory),
            "test-actor");

        dto.BindStrength.Should().Be(BindStrength.Mandatory);
    }

    [Fact]
    public async Task CreateAsync_NoAncestorPolicy_Allowed()
    {
        var (svc, scopes, _, db) = NewServices();
        var chain = await SeedChainAsync(scopes);
        var (_, version) = await SeedPolicyAsync(db, "fresh-pol");

        var dto = await svc.CreateAsync(
            new CreateBindingRequest(
                version.Id,
                BindingTargetType.ScopeNode,
                $"scope:{chain.Repo}",
                BindStrength.Recommended),
            "test-actor");

        dto.BindStrength.Should().Be(BindStrength.Recommended);
    }

    [Fact]
    public async Task DeleteAsync_NeverThrowsTightenOnlyViolation()
    {
        var (svc, scopes, _, db) = NewServices();
        var chain = await SeedChainAsync(scopes);
        var (_, version) = await SeedPolicyAsync(db, "del-pol");

        var dto = await svc.CreateAsync(
            new CreateBindingRequest(
                version.Id,
                BindingTargetType.ScopeNode,
                $"scope:{chain.Repo}",
                BindStrength.Mandatory),
            "test-actor");

        var act = async () => await svc.DeleteAsync(dto.Id, "test-actor", rationale: null);

        await act.Should().NotThrowAsync<TightenOnlyViolationException>();
    }
}
