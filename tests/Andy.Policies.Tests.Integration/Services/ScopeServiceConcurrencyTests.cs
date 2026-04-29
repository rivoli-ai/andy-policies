// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Andy.Policies.Tests.Integration.Services;

/// <summary>
/// P4.2 (#29) integration tests for <see cref="ScopeService"/> against a
/// real Postgres testcontainer. The unit suite covers happy-path CRUD
/// + walk semantics; this suite exercises the parts that depend on
/// real DB constraints — the unique <c>(Type, Ref)</c> index path and
/// concurrent-create races. Skipped silently when Docker is
/// unavailable.
/// </summary>
public class ScopeServiceConcurrencyTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("andy_policies_scope_concurrent")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
            _dockerAvailable = true;

            await using var setup = NewContext();
            await setup.Database.MigrateAsync();
        }
        catch (Exception)
        {
            _dockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private static ScopeService NewService(AppDbContext db) => new(db, TimeProvider.System);

    [SkippableFact]
    public async Task DuplicateTypeRefPair_ThrowsScopeRefConflict()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = NewContext();
        var svc = NewService(db);
        await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:dup-pg", "First"));

        var act = async () => await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:dup-pg", "Second"));

        var thrown = await act.Should().ThrowAsync<ScopeRefConflictException>();
        thrown.Which.Type.Should().Be(ScopeType.Org);
        thrown.Which.Ref.Should().Be("org:dup-pg");
    }

    [SkippableFact]
    public async Task SixLevelChain_GetAncestorsAsync_ReturnsFiveAncestors()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = NewContext();
        var svc = NewService(db);
        var org = await svc.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, "org:int", "Org"));
        var tenant = await svc.CreateAsync(new CreateScopeNodeRequest(
            org.Id, ScopeType.Tenant, "tenant:int", "Tenant"));
        var team = await svc.CreateAsync(new CreateScopeNodeRequest(
            tenant.Id, ScopeType.Team, "team:int", "Team"));
        var repo = await svc.CreateAsync(new CreateScopeNodeRequest(
            team.Id, ScopeType.Repo, "repo:int", "Repo"));
        var template = await svc.CreateAsync(new CreateScopeNodeRequest(
            repo.Id, ScopeType.Template, "template:int", "Template"));
        var run = await svc.CreateAsync(new CreateScopeNodeRequest(
            template.Id, ScopeType.Run, "run:int", "Run"));

        var ancestors = await svc.GetAncestorsAsync(run.Id);

        ancestors.Should().HaveCount(5);
        ancestors.Select(a => a.Id)
            .Should().ContainInOrder(org.Id, tenant.Id, team.Id, repo.Id, template.Id);
    }

    [SkippableFact]
    public async Task ConcurrentCreate_OfSameTypeRef_ExactlyOneSucceeds()
    {
        // Two services racing on the same (Type, Ref). The DB unique
        // index decides the winner; the loser surfaces as
        // ScopeRefConflictException so callers know to refresh + retry.
        Skip.IfNot(_dockerAvailable);

        const int N = 10;

        async Task<Outcome> RunAsync(int i)
        {
            await using var db = NewContext();
            var svc = NewService(db);
            try
            {
                await svc.CreateAsync(new CreateScopeNodeRequest(
                    null, ScopeType.Org, "org:race", $"Racer {i}"));
                return Outcome.Success;
            }
            catch (ScopeRefConflictException)
            {
                return Outcome.LostRace;
            }
        }

        var tasks = Enumerable.Range(0, N).Select(RunAsync);
        var results = await Task.WhenAll(tasks);

        results.Count(r => r == Outcome.Success).Should().Be(1,
            "exactly one of the N concurrent creators must commit");
        results.Count(r => r == Outcome.LostRace).Should().Be(N - 1);

        await using var verify = NewContext();
        var rows = await verify.ScopeNodes
            .AsNoTracking()
            .Where(s => s.Type == ScopeType.Org && s.Ref == "org:race")
            .ToListAsync();
        rows.Should().ContainSingle();
    }

    private enum Outcome { Success, LostRace }
}
