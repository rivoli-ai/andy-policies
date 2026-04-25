// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Protos;
using Andy.Policies.Tests.Integration.Controllers;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Andy.Policies.Tests.Integration.GrpcServices;

/// <summary>
/// End-to-end gRPC tests (P1.7, story rivoli-ai/andy-policies#77). Spins up
/// the API via <see cref="PoliciesApiFactory"/>, builds an in-process HTTP/2
/// channel against the test server, and exercises every RPC. Catches any
/// regression in the proto contract, generated stubs, exception mapping, or
/// service delegation.
/// </summary>
public class PolicyGrpcServiceTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;
    private readonly PolicyService.PolicyServiceClient _client;
    private readonly GrpcChannel _channel;

    public PolicyGrpcServiceTests(PoliciesApiFactory factory)
    {
        _factory = factory;
        var handler = factory.Server.CreateHandler();
        _channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
        _client = new PolicyService.PolicyServiceClient(_channel);
    }

    private static CreateDraftRequest MinimalCreate(string name) => new()
    {
        Name = name,
        Description = "",
        Summary = "summary",
        Enforcement = "Must",
        Severity = "Critical",
        RulesJson = "{}",
    };

    [Fact]
    public async Task ListPolicies_EmptyByDefault_ReturnsEmptyResponse()
    {
        var res = await _client.ListPoliciesAsync(new ListPoliciesRequest { NamePrefix = "no-such-zzz" });
        Assert.Empty(res.Policies);
    }

    [Fact]
    public async Task CreateDraft_Returns_VersionWithWireFormatCasing()
    {
        var slug = $"grpc-create-{Guid.NewGuid():N}".Substring(0, 16);
        var res = await _client.CreateDraftAsync(MinimalCreate(slug));

        Assert.Equal(1, res.Version.Version);
        Assert.Equal("Draft", res.Version.State);
        Assert.Equal("MUST", res.Version.Enforcement);    // ADR 0001 §6
        Assert.Equal("critical", res.Version.Severity);   // ADR 0001 §6
    }

    [Fact]
    public async Task CreateDraft_WithDuplicateSlug_ThrowsAlreadyExists()
    {
        var slug = $"grpc-dup-{Guid.NewGuid():N}".Substring(0, 16);
        await _client.CreateDraftAsync(MinimalCreate(slug));

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _client.CreateDraftAsync(MinimalCreate(slug)).ResponseAsync);
        Assert.Equal(StatusCode.AlreadyExists, ex.StatusCode);
    }

    [Fact]
    public async Task CreateDraft_WithInvalidName_ThrowsInvalidArgument()
    {
        var bad = MinimalCreate("BAD_SLUG"); // uppercase rejected by ADR 0001 §1
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _client.CreateDraftAsync(bad).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task GetPolicy_Found_ReturnsPolicy()
    {
        var slug = $"grpc-get-{Guid.NewGuid():N}".Substring(0, 16);
        var created = await _client.CreateDraftAsync(MinimalCreate(slug));

        var res = await _client.GetPolicyAsync(new GetPolicyRequest { Id = created.Version.PolicyId });

        Assert.Equal(slug, res.Policy.Name);
        Assert.Equal(1, res.Policy.VersionCount);
        Assert.False(res.Policy.HasActiveVersionId); // every version still Draft
    }

    [Fact]
    public async Task GetPolicy_Missing_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _client.GetPolicyAsync(new GetPolicyRequest { Id = Guid.NewGuid().ToString() }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task GetPolicy_InvalidGuid_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _client.GetPolicyAsync(new GetPolicyRequest { Id = "not-a-guid" }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task GetPolicyByName_RoundTrips()
    {
        var slug = $"grpc-byname-{Guid.NewGuid():N}".Substring(0, 16);
        await _client.CreateDraftAsync(MinimalCreate(slug));

        var res = await _client.GetPolicyByNameAsync(new GetPolicyByNameRequest { Name = slug });

        Assert.Equal(slug, res.Policy.Name);
    }

    [Fact]
    public async Task ListVersions_AfterCreate_ReturnsSingleEntry()
    {
        var slug = $"grpc-listver-{Guid.NewGuid():N}".Substring(0, 16);
        var created = await _client.CreateDraftAsync(MinimalCreate(slug));

        var res = await _client.ListVersionsAsync(new ListVersionsRequest { PolicyId = created.Version.PolicyId });

        Assert.Single(res.Versions);
        Assert.Equal(1, res.Versions[0].Version);
    }

    [Fact]
    public async Task GetVersion_RoundTrips()
    {
        var slug = $"grpc-getver-{Guid.NewGuid():N}".Substring(0, 16);
        var created = await _client.CreateDraftAsync(MinimalCreate(slug));

        var res = await _client.GetVersionAsync(new GetVersionRequest
        {
            PolicyId = created.Version.PolicyId,
            VersionId = created.Version.Id,
        });

        Assert.Equal(created.Version.Id, res.Version.Id);
    }

    [Fact]
    public async Task GetActiveVersion_AllDraft_ThrowsNotFound()
    {
        var slug = $"grpc-noactive-{Guid.NewGuid():N}".Substring(0, 16);
        var created = await _client.CreateDraftAsync(MinimalCreate(slug));

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _client.GetActiveVersionAsync(
                new GetActiveVersionRequest { PolicyId = created.Version.PolicyId }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task UpdateDraft_AppliesMutations()
    {
        var slug = $"grpc-update-{Guid.NewGuid():N}".Substring(0, 16);
        var created = await _client.CreateDraftAsync(MinimalCreate(slug));

        var updated = await _client.UpdateDraftAsync(new UpdateDraftRequest
        {
            PolicyId = created.Version.PolicyId,
            VersionId = created.Version.Id,
            Summary = "revised",
            Enforcement = "should",
            Severity = "moderate",
            RulesJson = "{\"allow\":true}",
        });

        Assert.Equal("revised", updated.Version.Summary);
        Assert.Equal("SHOULD", updated.Version.Enforcement);
        Assert.Equal("moderate", updated.Version.Severity);
    }

    [Fact]
    public async Task UpdateDraft_OnMissingVersion_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _client.UpdateDraftAsync(new UpdateDraftRequest
            {
                PolicyId = Guid.NewGuid().ToString(),
                VersionId = Guid.NewGuid().ToString(),
                Summary = "x",
                Enforcement = "must",
                Severity = "critical",
                RulesJson = "{}",
            }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task BumpDraft_WithOpenDraft_ThrowsAlreadyExists()
    {
        // ADR 0001 §4: only one open Draft per policy.
        var slug = $"grpc-bump-blocked-{Guid.NewGuid():N}".Substring(0, 16);
        var created = await _client.CreateDraftAsync(MinimalCreate(slug));

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _client.BumpDraftAsync(new BumpDraftRequest
            {
                PolicyId = created.Version.PolicyId,
                SourceVersionId = created.Version.Id,
            }).ResponseAsync);
        Assert.Equal(StatusCode.AlreadyExists, ex.StatusCode);
    }

    [Fact]
    public async Task BumpDraft_OnMissingPolicy_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _client.BumpDraftAsync(new BumpDraftRequest
            {
                PolicyId = Guid.NewGuid().ToString(),
                SourceVersionId = Guid.NewGuid().ToString(),
            }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task ListPolicies_FiltersAndPagination_PassThrough()
    {
        // Pagination round-trip — small slice over a few created policies.
        for (var i = 0; i < 3; i++)
        {
            await _client.CreateDraftAsync(MinimalCreate($"grpc-pag-{i:D2}-{Guid.NewGuid():N}".Substring(0, 16)));
        }

        var res = await _client.ListPoliciesAsync(new ListPoliciesRequest
        {
            NamePrefix = "grpc-pag-",
            Take = 2,
        });

        Assert.True(res.Policies.Count <= 2);
    }
}
