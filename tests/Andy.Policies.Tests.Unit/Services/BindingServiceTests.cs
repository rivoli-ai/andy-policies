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
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="BindingService"/> (P3.2, story
/// rivoli-ai/andy-policies#20). Drives the service over EF Core InMemory
/// to lock down: retired-version refusal, validation of <c>TargetRef</c>
/// length and emptiness, soft-delete tombstone shape, target-side query
/// case-sensitivity, and the audit-writer call sites.
/// </summary>
public class BindingServiceTests
{
    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<(string Action, Guid EntityId, string Actor, string? Rationale)> Calls { get; } = new();

        public Task AppendAsync(string action, Guid entityId, string actorSubjectId, string? rationale, CancellationToken ct = default)
        {
            Calls.Add((action, entityId, actorSubjectId, rationale));
            return Task.CompletedTask;
        }
    }

    private static (BindingService service, AppDbContext db, RecordingAuditWriter audit) NewService()
    {
        var db = InMemoryDbFixture.Create();
        var audit = new RecordingAuditWriter();
        var service = new BindingService(db, audit, TimeProvider.System);
        return (service, db, audit);
    }

    private static async Task<PolicyVersion> SeedVersionAsync(AppDbContext db, LifecycleState state)
    {
        var policy = PolicyBuilders.APolicy(name: $"binding-{Guid.NewGuid():N}".Substring(0, 16));
        var version = PolicyBuilders.AVersion(policy.Id, number: 1, state: state);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
        return version;
    }

    private static CreateBindingRequest MinimalRequest(Guid versionId, string targetRef = "repo:rivoli-ai/andy-policies")
        => new(versionId, BindingTargetType.Repo, targetRef, BindStrength.Mandatory);

    [Fact]
    public async Task CreateAsync_OnDraftVersion_Succeeds_AndRecordsAudit()
    {
        var (svc, db, audit) = NewService();
        var version = await SeedVersionAsync(db, LifecycleState.Draft);

        var dto = await svc.CreateAsync(MinimalRequest(version.Id), "sam");

        dto.PolicyVersionId.Should().Be(version.Id);
        dto.TargetType.Should().Be(BindingTargetType.Repo);
        dto.TargetRef.Should().Be("repo:rivoli-ai/andy-policies");
        dto.BindStrength.Should().Be(BindStrength.Mandatory);
        dto.CreatedBySubjectId.Should().Be("sam");
        dto.DeletedAt.Should().BeNull();

        audit.Calls.Should().ContainSingle()
            .Which.Should().Match<(string Action, Guid EntityId, string Actor, string? Rationale)>(c =>
                c.Action == "binding.created" && c.EntityId == dto.Id && c.Actor == "sam");
    }

    [Theory]
    [InlineData(LifecycleState.Active)]
    [InlineData(LifecycleState.WindingDown)]
    public async Task CreateAsync_OnActiveOrWindingDownVersion_Succeeds(LifecycleState state)
    {
        // Only Retired refuses new bindings — Active and WindingDown both
        // accept binds (Active is the dominant case; WindingDown is allowed
        // so consumers can still author bindings during a sunset window).
        var (svc, db, _) = NewService();
        var version = await SeedVersionAsync(db, state);

        var dto = await svc.CreateAsync(MinimalRequest(version.Id), "sam");

        dto.PolicyVersionId.Should().Be(version.Id);
    }

    [Fact]
    public async Task CreateAsync_OnRetiredVersion_ThrowsBindingRetiredVersionException()
    {
        var (svc, db, audit) = NewService();
        var version = await SeedVersionAsync(db, LifecycleState.Retired);

        var act = async () => await svc.CreateAsync(MinimalRequest(version.Id), "sam");

        var thrown = await act.Should().ThrowAsync<BindingRetiredVersionException>();
        thrown.Which.PolicyVersionId.Should().Be(version.Id);
        audit.Calls.Should().BeEmpty("no audit row when the create failed validation");
    }

    [Fact]
    public async Task CreateAsync_OnUnknownVersionId_ThrowsNotFound()
    {
        var (svc, _, _) = NewService();

        var act = async () => await svc.CreateAsync(MinimalRequest(Guid.NewGuid()), "sam");

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public async Task CreateAsync_WithEmptyOrWhitespaceTargetRef_ThrowsValidation(string ref_)
    {
        var (svc, db, _) = NewService();
        var version = await SeedVersionAsync(db, LifecycleState.Draft);

        var act = async () => await svc.CreateAsync(MinimalRequest(version.Id, ref_), "sam");

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateAsync_WithOversizedTargetRef_ThrowsValidation()
    {
        var (svc, db, _) = NewService();
        var version = await SeedVersionAsync(db, LifecycleState.Draft);
        var oversized = new string('a', 513);

        var act = async () => await svc.CreateAsync(MinimalRequest(version.Id, oversized), "sam");

        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*512*");
    }

    [Fact]
    public async Task DeleteAsync_StampsTombstoneAndRecordsAudit()
    {
        var (svc, db, audit) = NewService();
        var version = await SeedVersionAsync(db, LifecycleState.Active);
        var binding = await svc.CreateAsync(MinimalRequest(version.Id), "sam");
        audit.Calls.Clear();

        await svc.DeleteAsync(binding.Id, "sam", rationale: "no longer applies");

        var reloaded = await db.Bindings.AsNoTracking().FirstAsync(b => b.Id == binding.Id);
        reloaded.DeletedAt.Should().NotBeNull();
        reloaded.DeletedBySubjectId.Should().Be("sam");

        audit.Calls.Should().ContainSingle()
            .Which.Should().Match<(string Action, Guid EntityId, string Actor, string? Rationale)>(c =>
                c.Action == "binding.deleted" && c.EntityId == binding.Id
                && c.Actor == "sam" && c.Rationale == "no longer applies");
    }

    [Fact]
    public async Task DeleteAsync_OnAlreadyDeletedBinding_ThrowsNotFound()
    {
        var (svc, db, _) = NewService();
        var version = await SeedVersionAsync(db, LifecycleState.Active);
        var binding = await svc.CreateAsync(MinimalRequest(version.Id), "sam");
        await svc.DeleteAsync(binding.Id, "sam", rationale: null);

        var act = async () => await svc.DeleteAsync(binding.Id, "sam", rationale: null);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_OnUnknownId_ThrowsNotFound()
    {
        var (svc, _, _) = NewService();

        var act = async () => await svc.DeleteAsync(Guid.NewGuid(), "sam", rationale: null);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ListByPolicyVersionAsync_ExcludesTombstonedRows_WhenIncludeDeletedFalse()
    {
        var (svc, db, _) = NewService();
        var version = await SeedVersionAsync(db, LifecycleState.Active);
        var alive = await svc.CreateAsync(MinimalRequest(version.Id, "repo:a/b"), "sam");
        var dead  = await svc.CreateAsync(MinimalRequest(version.Id, "repo:c/d"), "sam");
        await svc.DeleteAsync(dead.Id, "sam", rationale: null);

        var visible = await svc.ListByPolicyVersionAsync(version.Id, includeDeleted: false);
        visible.Should().ContainSingle().Which.Id.Should().Be(alive.Id);

        var all = await svc.ListByPolicyVersionAsync(version.Id, includeDeleted: true);
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListByTargetAsync_ExactMatchOnly_NoCaseFolding()
    {
        var (svc, db, _) = NewService();
        var version = await SeedVersionAsync(db, LifecycleState.Active);
        await svc.CreateAsync(
            new CreateBindingRequest(version.Id, BindingTargetType.Repo, "repo:rivoli-ai/policy-x", BindStrength.Recommended),
            "sam");
        await svc.CreateAsync(
            new CreateBindingRequest(version.Id, BindingTargetType.Repo, "repo:RIVOLI-AI/POLICY-X", BindStrength.Recommended),
            "sam");

        var lower = await svc.ListByTargetAsync(BindingTargetType.Repo, "repo:rivoli-ai/policy-x");
        var upper = await svc.ListByTargetAsync(BindingTargetType.Repo, "repo:RIVOLI-AI/POLICY-X");

        lower.Should().ContainSingle().Which.TargetRef.Should().Be("repo:rivoli-ai/policy-x");
        upper.Should().ContainSingle().Which.TargetRef.Should().Be("repo:RIVOLI-AI/POLICY-X");
    }

    [Fact]
    public async Task ListByTargetAsync_FiltersOutDeletedBindings()
    {
        var (svc, db, _) = NewService();
        var version = await SeedVersionAsync(db, LifecycleState.Active);
        var alive = await svc.CreateAsync(MinimalRequest(version.Id, "tenant:abc"), "sam");
        var dead  = await svc.CreateAsync(MinimalRequest(version.Id, "tenant:abc"), "sam");
        await svc.DeleteAsync(dead.Id, "sam", rationale: null);

        var results = await svc.ListByTargetAsync(BindingTargetType.Repo, "tenant:abc");

        results.Should().ContainSingle().Which.Id.Should().Be(alive.Id);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForUnknownId()
    {
        var (svc, _, _) = NewService();

        var dto = await svc.GetAsync(Guid.NewGuid());

        dto.Should().BeNull();
    }
}
