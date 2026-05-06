// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.GrpcServices;

/// <summary>
/// End-to-end gRPC tests for <see cref="Andy.Policies.Api.GrpcServices.BundleGrpcService"/>
/// (P8.6, story rivoli-ai/andy-policies#86). Each RPC is exercised
/// over an in-process HTTP/2 channel to confirm the proto contract,
/// the generated stubs, the exception → status mapping, and the
/// shared service-layer wiring (no duplicated business logic vs.
/// REST/MCP). The diff RPC also pins byte-identical output across
/// two invocations.
/// </summary>
public class BundleGrpcServiceTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;
    private readonly BundleService.BundleServiceClient _client;
    private readonly GrpcChannel _channel;

    public BundleGrpcServiceTests(PoliciesApiFactory factory)
    {
        _factory = factory;
        var handler = factory.Server.CreateHandler();
        _channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
        _client = new BundleService.BundleServiceClient(_channel);
    }

    private async Task SeedActiveVersionAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = slug,
            CreatedBySubjectId = "seed",
        };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            Version = 1,
            State = LifecycleState.Active,
            Enforcement = EnforcementLevel.Should,
            Severity = Severity.Moderate,
            Scopes = new List<string>(),
            Summary = "fixture",
            RulesJson = "{}",
            CreatedBySubjectId = "seed",
            ProposerSubjectId = "seed",
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedBySubjectId = "seed",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CreateBundle_HappyPath_ReturnsBundleMessage_WithSnapshotHash()
    {
        await SeedActiveVersionAsync($"p-{Guid.NewGuid():N}".Substring(0, 12));
        var name = $"snap-{Guid.NewGuid():N}".Substring(0, 16);

        var resp = await _client.CreateBundleAsync(new Andy.Policies.Api.Protos.CreateBundleRequest
        {
            Name = name,
            Rationale = "initial",
        });

        resp.Name.Should().Be(name);
        resp.SnapshotHash.Should().HaveLength(64);
        resp.State.Should().Be("Active");
    }

    [Fact]
    public async Task CreateBundle_DuplicateActiveName_ThrowsAlreadyExists()
    {
        await SeedActiveVersionAsync($"p-{Guid.NewGuid():N}".Substring(0, 12));
        var name = $"dup-{Guid.NewGuid():N}".Substring(0, 16);
        await _client.CreateBundleAsync(new Andy.Policies.Api.Protos.CreateBundleRequest
        {
            Name = name, Rationale = "first",
        });

        var act = async () => await _client.CreateBundleAsync(new Andy.Policies.Api.Protos.CreateBundleRequest
        {
            Name = name, Rationale = "second",
        }).ResponseAsync;

        var ex = await Assert.ThrowsAsync<RpcException>(act);
        ex.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    [Fact]
    public async Task GetBundle_UnknownId_ThrowsNotFound()
    {
        var act = async () => await _client.GetBundleAsync(new GetBundleRequest
        {
            Id = Guid.NewGuid().ToString(),
        }).ResponseAsync;

        var ex = await Assert.ThrowsAsync<RpcException>(act);
        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task GetBundle_BadGuid_ThrowsInvalidArgument()
    {
        var act = async () => await _client.GetBundleAsync(new GetBundleRequest
        {
            Id = "not-a-guid",
        }).ResponseAsync;

        var ex = await Assert.ThrowsAsync<RpcException>(act);
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ListBundles_ReturnsCreatedBundles()
    {
        await SeedActiveVersionAsync($"p-{Guid.NewGuid():N}".Substring(0, 12));
        var name = $"list-{Guid.NewGuid():N}".Substring(0, 16);
        var created = await _client.CreateBundleAsync(new Andy.Policies.Api.Protos.CreateBundleRequest
        {
            Name = name, Rationale = "x",
        });

        var resp = await _client.ListBundlesAsync(new ListBundlesRequest { Take = 200 });

        resp.Bundles.Should().Contain(b => b.Id == created.Id);
    }

    [Fact]
    public async Task DeleteBundle_SoftFlips_AndAuditRowAppears()
    {
        await SeedActiveVersionAsync($"p-{Guid.NewGuid():N}".Substring(0, 12));
        var name = $"del-{Guid.NewGuid():N}".Substring(0, 16);
        var created = await _client.CreateBundleAsync(new Andy.Policies.Api.Protos.CreateBundleRequest
        {
            Name = name, Rationale = "x",
        });

        var deleted = await _client.DeleteBundleAsync(new DeleteBundleRequest
        {
            Id = created.Id, Rationale = "decommission",
        });

        deleted.State.Should().Be("Deleted");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditCount = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == "bundle.delete" && e.EntityId == created.Id)
            .CountAsync();
        auditCount.Should().Be(1);
    }

    [Fact]
    public async Task DiffBundles_TwoInvocationsOnSamePair_ProduceByteIdenticalPatch()
    {
        await SeedActiveVersionAsync($"p-{Guid.NewGuid():N}".Substring(0, 12));
        var nameA = $"diff-a-{Guid.NewGuid():N}".Substring(0, 14);
        var nameB = $"diff-b-{Guid.NewGuid():N}".Substring(0, 14);
        var a = await _client.CreateBundleAsync(new Andy.Policies.Api.Protos.CreateBundleRequest
        {
            Name = nameA, Rationale = "x",
        });
        var b = await _client.CreateBundleAsync(new Andy.Policies.Api.Protos.CreateBundleRequest
        {
            Name = nameB, Rationale = "x",
        });

        var first = await _client.DiffBundlesAsync(new DiffBundlesRequest { FromId = a.Id, ToId = b.Id });
        var second = await _client.DiffBundlesAsync(new DiffBundlesRequest { FromId = a.Id, ToId = b.Id });

        second.Rfc6902PatchJson.Should().Be(
            first.Rfc6902PatchJson,
            "diff determinism is the floor under reproducibility — gRPC consumers " +
            "rely on byte-identical patches for caching + idempotent retries");
        first.FromSnapshotHash.Should().Be(a.SnapshotHash);
        first.ToSnapshotHash.Should().Be(b.SnapshotHash);
    }

    [Fact]
    public async Task DiffBundles_UnknownBundle_ThrowsNotFound()
    {
        await SeedActiveVersionAsync($"p-{Guid.NewGuid():N}".Substring(0, 12));
        var present = await _client.CreateBundleAsync(new Andy.Policies.Api.Protos.CreateBundleRequest
        {
            Name = $"diff-{Guid.NewGuid():N}".Substring(0, 14), Rationale = "x",
        });

        var act = async () => await _client.DiffBundlesAsync(new DiffBundlesRequest
        {
            FromId = present.Id,
            ToId = Guid.NewGuid().ToString(),
        }).ResponseAsync;

        var ex = await Assert.ThrowsAsync<RpcException>(act);
        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task ResolveBundle_UnknownId_ThrowsNotFound()
    {
        var act = async () => await _client.ResolveBundleAsync(new ResolveBundleRequest
        {
            Id = Guid.NewGuid().ToString(),
            TargetType = "Repo",
            TargetRef = "repo:any",
        }).ResponseAsync;

        var ex = await Assert.ThrowsAsync<RpcException>(act);
        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task ResolveBundle_BadTargetType_ThrowsInvalidArgument()
    {
        var act = async () => await _client.ResolveBundleAsync(new ResolveBundleRequest
        {
            Id = Guid.NewGuid().ToString(),
            TargetType = "Unicorn",
            TargetRef = "ref",
        }).ResponseAsync;

        var ex = await Assert.ThrowsAsync<RpcException>(act);
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
