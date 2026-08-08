// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Andy.Policies.Tests.Integration.Services;

public sealed class MutationAuditAtomicityTests
{
    [Fact]
    public async Task PolicyCreate_AuditFailureRollsBackState()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var service = new PolicyService(
            fixture.Db, new ThrowingAuditWriter(), IntegrationAllowAnyRationalePolicy.Instance);

        var act = () => service.CreateDraftAsync(new CreatePolicyRequest(
            "atomic-policy", null, "summary", "Must", "Critical",
            Array.Empty<string>(), "{}", "test rationale"), "user:alice");

        await act.Should().ThrowAsync<AuditFailureException>();
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Policies.CountAsync()).Should().Be(0);
        (await fixture.Db.PolicyVersions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BindingCreate_AuditFailureRollsBackState()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var version = await SeedVersionAsync(fixture.Db);
        var service = new BindingService(
            fixture.Db, new ThrowingAuditWriter(), TimeProvider.System,
            null, IntegrationAllowAnyRationalePolicy.Instance);

        var act = () => service.CreateAsync(new CreateBindingRequest(
            version.Id, BindingTargetType.Repo, "repo:acme/svc",
            BindStrength.Mandatory, "test rationale"), "user:alice");

        await act.Should().ThrowAsync<AuditFailureException>();
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Bindings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ScopeCreate_AuditFailureRollsBackState()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var service = new ScopeService(
            fixture.Db, TimeProvider.System, new ThrowingAuditWriter(),
            IntegrationAllowAnyRationalePolicy.Instance);

        var act = () => service.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:acme", "Acme", "test rationale"), "user:alice");

        await act.Should().ThrowAsync<AuditFailureException>();
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.ScopeNodes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ItemCreate_AuditFailureRollsBackState()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var service = new ItemService(
            fixture.Db, new ThrowingAuditWriter(), IntegrationAllowAnyRationalePolicy.Instance);

        var act = () => service.CreateAsync(
            new CreateItemRequest("item", "description", "test rationale"), "user:alice");

        await act.Should().ThrowAsync<AuditFailureException>();
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Items.CountAsync()).Should().Be(0);
    }

    private static async Task<PolicyVersion> SeedVersionAsync(AppDbContext db)
    {
        var policy = new Policy { Id = Guid.NewGuid(), Name = "seed", CreatedBySubjectId = "test" };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(), PolicyId = policy.Id, Version = 1,
            State = LifecycleState.Active, Enforcement = EnforcementLevel.Must,
            Severity = Severity.Critical, Scopes = new List<string>(),
            Summary = "seed", RulesJson = "{}", CreatedBySubjectId = "test",
            ProposerSubjectId = "test",
        };
        db.AddRange(policy, version);
        await db.SaveChangesAsync();
        return version;
    }

    private sealed class ThrowingAuditWriter : IAuditWriter
    {
        public Task AppendAsync(string action, Guid entityId, string actorSubjectId,
            string? rationale, CancellationToken ct = default) =>
            throw new AuditFailureException();
    }

    private sealed class AuditFailureException : Exception;

    private sealed class SqliteFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Db { get; }

        private SqliteFixture(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public static async Task<SqliteFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);
            await db.Database.EnsureCreatedAsync();
            return new SqliteFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
