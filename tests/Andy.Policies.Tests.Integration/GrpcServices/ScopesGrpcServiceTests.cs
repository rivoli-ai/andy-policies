// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Protos;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using ProtoScopeType = Andy.Policies.Api.Protos.ScopeType;

namespace Andy.Policies.Tests.Integration.GrpcServices;

/// <summary>
/// End-to-end gRPC tests for the scope surface (P4.6, story
/// rivoli-ai/andy-policies#34). Exercises every RPC over a real HTTP/2
/// channel against the test server, verifying the proto contract,
/// generated stubs, exception → status-code mapping, and parity with
/// the REST/MCP surfaces (ladder enforcement, ref-conflict, has-
/// descendants, effective-policies envelope).
/// </summary>
public class ScopesGrpcServiceTests : IClassFixture<PoliciesApiFactory>, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly Andy.Policies.Api.Protos.ScopesService.ScopesServiceClient _scopes;

    public ScopesGrpcServiceTests(PoliciesApiFactory factory)
    {
        var handler = factory.Server.CreateHandler();
        _channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
        _scopes = new Andy.Policies.Api.Protos.ScopesService.ScopesServiceClient(_channel);
    }

    public void Dispose() => _channel.Dispose();

    private static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}".Substring(0, 20);

    private async Task<ScopeNodeMessage> CreateOrgAsync(string @ref)
    {
        var resp = await _scopes.CreateScopeAsync(new CreateScopeRequest
        {
            ParentId = string.Empty,
            Type = ProtoScopeType.Org,
            TargetRef = @ref,
            DisplayName = "Org",
        });
        return resp.Node;
    }

    [Fact]
    public async Task CreateScope_OnRoot_ReturnsPopulatedNode()
    {
        var orgRef = Slug("org:grpc-create");
        var resp = await _scopes.CreateScopeAsync(new CreateScopeRequest
        {
            ParentId = string.Empty,
            Type = ProtoScopeType.Org,
            TargetRef = orgRef,
            DisplayName = "Org",
        });

        resp.Node.ParentId.Should().BeEmpty();
        resp.Node.Type.Should().Be(ProtoScopeType.Org);
        resp.Node.TargetRef.Should().Be(orgRef);
        resp.Node.Depth.Should().Be(0);
    }

    [Fact]
    public async Task CreateScope_WithUnspecifiedType_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _scopes.CreateScopeAsync(new CreateScopeRequest
            {
                ParentId = string.Empty,
                Type = ProtoScopeType.Unspecified,
                TargetRef = Slug("org:grpc-uns"),
                DisplayName = "X",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task CreateScope_LadderViolation_ThrowsFailedPrecondition()
    {
        var org = await CreateOrgAsync(Slug("org:grpc-lad"));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _scopes.CreateScopeAsync(new CreateScopeRequest
            {
                ParentId = org.Id,
                Type = ProtoScopeType.Team,  // Team must parent a Tenant.
                TargetRef = Slug("team:bad"),
                DisplayName = "Bad",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task CreateScope_DuplicateTypeRef_ThrowsAlreadyExists()
    {
        var refValue = Slug("org:grpc-dup");
        await CreateOrgAsync(refValue);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _scopes.CreateScopeAsync(new CreateScopeRequest
            {
                ParentId = string.Empty,
                Type = ProtoScopeType.Org,
                TargetRef = refValue,
                DisplayName = "Second",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    [Fact]
    public async Task CreateScope_WithMissingParent_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _scopes.CreateScopeAsync(new CreateScopeRequest
            {
                ParentId = Guid.NewGuid().ToString(),
                Type = ProtoScopeType.Tenant,
                TargetRef = Slug("tenant:orphan"),
                DisplayName = "Orphan",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task GetScope_RoundTripsAfterCreate_AndThrowsNotFoundForUnknownId()
    {
        var org = await CreateOrgAsync(Slug("org:grpc-get"));
        var resp = await _scopes.GetScopeAsync(new GetScopeRequest { Id = org.Id });
        resp.Node.Id.Should().Be(org.Id);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _scopes.GetScopeAsync(new GetScopeRequest { Id = Guid.NewGuid().ToString() }).ResponseAsync);
        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task ListScopes_WithFilter_ReturnsOnlyMatching()
    {
        await CreateOrgAsync(Slug("org:grpc-fl"));

        var resp = await _scopes.ListScopesAsync(new ListScopesRequest
        {
            Type = ProtoScopeType.Org,
        });

        resp.Nodes.Should().NotBeEmpty();
        resp.Nodes.All(n => n.Type == ProtoScopeType.Org).Should().BeTrue();
    }

    [Fact]
    public async Task GetScopeTree_ReturnsForestWithExpectedShape()
    {
        var orgRef = Slug("org:grpc-tree");
        var org = await CreateOrgAsync(orgRef);
        await _scopes.CreateScopeAsync(new CreateScopeRequest
        {
            ParentId = org.Id,
            Type = ProtoScopeType.Tenant,
            TargetRef = Slug("tenant:grpc-tree"),
            DisplayName = "Tenant",
        });

        var resp = await _scopes.GetScopeTreeAsync(new GetScopeTreeRequest());

        var orgTree = resp.Forest.FirstOrDefault(t => t.Node.Id == org.Id);
        orgTree.Should().NotBeNull();
        orgTree!.Children.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteScope_OnLeaf_Succeeds_AndDoubleDeleteThrowsNotFound()
    {
        var org = await CreateOrgAsync(Slug("org:grpc-del"));

        await _scopes.DeleteScopeAsync(new DeleteScopeRequest { Id = org.Id });

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _scopes.DeleteScopeAsync(new DeleteScopeRequest { Id = org.Id }).ResponseAsync);
        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteScope_OnNonLeaf_ThrowsFailedPrecondition()
    {
        var org = await CreateOrgAsync(Slug("org:grpc-non"));
        await _scopes.CreateScopeAsync(new CreateScopeRequest
        {
            ParentId = org.Id,
            Type = ProtoScopeType.Tenant,
            TargetRef = Slug("tenant:grpc-non"),
            DisplayName = "Tn",
        });

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _scopes.DeleteScopeAsync(new DeleteScopeRequest { Id = org.Id }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task GetEffectivePolicies_ReturnsEnvelopeForKnownScope()
    {
        var org = await CreateOrgAsync(Slug("org:grpc-eff"));

        var resp = await _scopes.GetEffectivePoliciesAsync(new GetEffectivePoliciesRequest { Id = org.Id });

        resp.ScopeNodeId.Should().Be(org.Id);
        resp.Policies.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEffectivePolicies_OnUnknownScope_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _scopes.GetEffectivePoliciesAsync(new GetEffectivePoliciesRequest
            {
                Id = Guid.NewGuid().ToString(),
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task GetScope_InvalidGuid_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _scopes.GetScopeAsync(new GetScopeRequest { Id = "not-a-guid" }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
