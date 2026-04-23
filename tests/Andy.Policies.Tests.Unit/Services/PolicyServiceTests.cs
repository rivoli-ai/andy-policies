// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Queries;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

public class PolicyServiceTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // The service opens an explicit transaction around CreateDraft / BumpDraft
            // for atomicity — InMemory cannot honour transactions but the logical test
            // semantics are unchanged. Suppress the warning so tests run.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static CreatePolicyRequest MinimalCreate(string name, string? scope = null) => new(
        Name: name,
        Description: null,
        Summary: "summary",
        Enforcement: "Must",
        Severity: "Critical",
        Scopes: scope is null ? Array.Empty<string>() : new[] { scope },
        RulesJson: "{}");

    [Fact]
    public async Task CreateDraftAsync_AssignsVersionOne_ForNewPolicy()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);

        var dto = await service.CreateDraftAsync(MinimalCreate("no-prod"), "sam");

        Assert.Equal(1, dto.Version);
        Assert.Equal("Draft", dto.State);
        Assert.Equal("MUST", dto.Enforcement);
        Assert.Equal("critical", dto.Severity);
        Assert.Equal("sam", dto.CreatedBySubjectId);
        Assert.Equal("sam", dto.ProposerSubjectId);
    }

    [Fact]
    public async Task CreateDraftAsync_WhenSlugDuplicate_ThrowsConflictException()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        await service.CreateDraftAsync(MinimalCreate("high-risk"), "sam");

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => service.CreateDraftAsync(MinimalCreate("high-risk"), "alice"));

        Assert.Contains("high-risk", ex.Message);
    }

    [Fact]
    public async Task CreateDraftAsync_WhenScopeContainsWildcard_ThrowsValidationException()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);

        await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateDraftAsync(MinimalCreate("bad-scope", scope: "*"), "sam"));
    }

    [Fact]
    public async Task CreateDraftAsync_WhenNameInvalid_ThrowsValidationException()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateDraftAsync(MinimalCreate("BadSlug"), "sam"));
        Assert.Contains("BadSlug", ex.Message);
    }

    [Fact]
    public async Task CreateDraftAsync_WhenRulesJsonMalformed_ThrowsValidationException()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var request = MinimalCreate("bad-json") with { RulesJson = "{not: 'json'" };

        await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateDraftAsync(request, "sam"));
    }

    [Fact]
    public async Task CreateDraftAsync_WhenRulesJsonTooLarge_ThrowsValidationException()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        // 65 KB — just over the 64 KB cap.
        var oversized = "{\"x\":\"" + new string('a', 64 * 1024 + 100) + "\"}";
        var request = MinimalCreate("big") with { RulesJson = oversized };

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateDraftAsync(request, "sam"));
        Assert.Contains("cap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateDraftAsync_CanonicalisesScopes()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var req = MinimalCreate("canonical") with { Scopes = new[] { "tool:write", "prod", "tool:write" } };

        var dto = await service.CreateDraftAsync(req, "sam");

        Assert.Equal(new[] { "prod", "tool:write" }, dto.Scopes);
    }

    [Fact]
    public async Task UpdateDraftAsync_MutatesFields_InDraftState()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var created = await service.CreateDraftAsync(MinimalCreate("mutable"), "sam");

        var updated = await service.UpdateDraftAsync(
            created.PolicyId, created.Id,
            new UpdatePolicyVersionRequest(
                Summary: "revised",
                Enforcement: "should",
                Severity: "moderate",
                Scopes: new[] { "staging" },
                RulesJson: "{\"allow\":[\"read\"]}"),
            "alice");

        Assert.Equal("revised", updated.Summary);
        Assert.Equal("SHOULD", updated.Enforcement);
        Assert.Equal("moderate", updated.Severity);
        Assert.Equal(new[] { "staging" }, updated.Scopes);
    }

    [Fact]
    public async Task UpdateDraftAsync_WhenVersionIsPublished_ThrowsInvalidOperationException()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var created = await service.CreateDraftAsync(MinimalCreate("published"), "sam");

        // Forcefully transition the underlying entity to Active (P2 territory — simulated here).
        var entity = await db.PolicyVersions.FirstAsync(v => v.Id == created.Id);
        entity.State = LifecycleState.Active;
        await db.SaveChangesAsync();

        var req = new UpdatePolicyVersionRequest("x", "must", "critical", Array.Empty<string>(), "{}");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateDraftAsync(created.PolicyId, created.Id, req, "sam"));
    }

    [Fact]
    public async Task UpdateDraftAsync_WhenVersionNotFound_ThrowsNotFoundException()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var req = new UpdatePolicyVersionRequest("x", "must", "critical", Array.Empty<string>(), "{}");

        await Assert.ThrowsAsync<NotFoundException>(
            () => service.UpdateDraftAsync(Guid.NewGuid(), Guid.NewGuid(), req, "sam"));
    }

    [Fact]
    public async Task BumpDraftFromVersionAsync_AssignsNextVersionNumber()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var v1 = await service.CreateDraftAsync(MinimalCreate("bump-target"), "sam");

        // Simulate P2 publish so the draft-count guard does not fire.
        var entity = await db.PolicyVersions.FirstAsync(v => v.Id == v1.Id);
        entity.State = LifecycleState.Active;
        await db.SaveChangesAsync();

        var v2 = await service.BumpDraftFromVersionAsync(v1.PolicyId, v1.Id, "alice");

        Assert.Equal(2, v2.Version);
        Assert.Equal("Draft", v2.State);
        Assert.Equal("alice", v2.CreatedBySubjectId);
    }

    [Fact]
    public async Task BumpDraftFromVersionAsync_WhenOpenDraftExists_ThrowsConflictException()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var v1 = await service.CreateDraftAsync(MinimalCreate("has-draft"), "sam");
        // v1 is still Draft — a bump attempt must refuse.

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => service.BumpDraftFromVersionAsync(v1.PolicyId, v1.Id, "alice"));

        Assert.Contains("has-draft", ex.Message);
        Assert.Contains("draft", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BumpDraftFromVersionAsync_WhenPolicyMissing_ThrowsNotFoundException()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);

        await Assert.ThrowsAsync<NotFoundException>(
            () => service.BumpDraftFromVersionAsync(Guid.NewGuid(), Guid.NewGuid(), "sam"));
    }

    [Fact]
    public async Task GetActiveVersionAsync_ReturnsHighestNonDraft()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var v1 = await service.CreateDraftAsync(MinimalCreate("active"), "sam");
        var entity = await db.PolicyVersions.FirstAsync(v => v.Id == v1.Id);
        entity.State = LifecycleState.Active;
        await db.SaveChangesAsync();

        var active = await service.GetActiveVersionAsync(v1.PolicyId);

        Assert.NotNull(active);
        Assert.Equal(v1.Id, active!.Id);
    }

    [Fact]
    public async Task GetActiveVersionAsync_ReturnsNull_WhenAllDraft()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var v1 = await service.CreateDraftAsync(MinimalCreate("all-draft"), "sam");

        var active = await service.GetActiveVersionAsync(v1.PolicyId);

        Assert.Null(active);
    }

    [Fact]
    public async Task ListPoliciesAsync_FiltersByScope_AgainstActiveVersion()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);

        // Two published policies, one scoped to prod.
        var a = await service.CreateDraftAsync(MinimalCreate("a-policy", scope: "prod"), "sam");
        var b = await service.CreateDraftAsync(MinimalCreate("b-policy", scope: "staging"), "sam");

        foreach (var id in new[] { a.Id, b.Id })
        {
            var e = await db.PolicyVersions.FirstAsync(v => v.Id == id);
            e.State = LifecycleState.Active;
        }
        await db.SaveChangesAsync();

        var results = await service.ListPoliciesAsync(new ListPoliciesQuery(Scope: "prod"));

        Assert.Single(results);
        Assert.Equal("a-policy", results[0].Name);
    }

    [Fact]
    public async Task ListPoliciesAsync_ExcludesPoliciesWithNoActiveVersion_WhenFilterIsApplied()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        await service.CreateDraftAsync(MinimalCreate("never-published", scope: "prod"), "sam");

        // A filter-scoped query on a policy that's still Draft should return nothing.
        var filtered = await service.ListPoliciesAsync(new ListPoliciesQuery(Scope: "prod"));
        Assert.Empty(filtered);

        // An unfiltered query still returns the policy (it exists, even if unpublished).
        var unfiltered = await service.ListPoliciesAsync(new ListPoliciesQuery());
        Assert.Single(unfiltered);
    }

    [Fact]
    public async Task ListPoliciesAsync_RespectsPagination()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        for (var i = 0; i < 5; i++)
        {
            await service.CreateDraftAsync(MinimalCreate($"p-{i}"), "sam");
        }

        var page = await service.ListPoliciesAsync(new ListPoliciesQuery(Skip: 2, Take: 2));

        Assert.Equal(2, page.Count);
        Assert.Equal("p-2", page[0].Name);
        Assert.Equal("p-3", page[1].Name);
    }

    [Fact]
    public async Task ListPoliciesAsync_CapsTakeAtFiveHundred()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        // Request well above the cap; service should not crash, just return whatever we have.
        var results = await service.ListPoliciesAsync(new ListPoliciesQuery(Take: 10_000));

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetPolicyAsync_ReturnsNull_WhenMissing()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);

        var result = await service.GetPolicyAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task PolicyDto_ReportsVersionCountAndActiveVersionId()
    {
        using var db = CreateInMemoryDb();
        var service = new PolicyService(db);
        var v1 = await service.CreateDraftAsync(MinimalCreate("count-me"), "sam");

        // Still all Draft — ActiveVersionId should be null.
        var before = await service.GetPolicyAsync(v1.PolicyId);
        Assert.NotNull(before);
        Assert.Equal(1, before!.VersionCount);
        Assert.Null(before.ActiveVersionId);

        // Transition to Active — ActiveVersionId tracks.
        var e = await db.PolicyVersions.FirstAsync(v => v.Id == v1.Id);
        e.State = LifecycleState.Active;
        await db.SaveChangesAsync();

        var after = await service.GetPolicyAsync(v1.PolicyId);
        Assert.Equal(v1.Id, after!.ActiveVersionId);
    }
}
