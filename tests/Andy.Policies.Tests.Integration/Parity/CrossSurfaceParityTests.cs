// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using Andy.Policies.Api.Mcp;
using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Parity;

/// <summary>
/// P1.11 (#91): every read surface (REST, MCP, gRPC) must return semantically
/// equivalent results for the same operation. The CLI is a thin REST client
/// (P1.8) so its parity is implied by the REST assertion — a separate
/// subprocess-based golden test would couple this suite to dotnet-run startup
/// time without adding signal beyond what we already get here.
///
/// All four surfaces resolve through the same <see cref="IPolicyService"/>
/// instance under <see cref="PoliciesApiFactory"/>; this fixture proves the
/// surface-specific serializers don't drift.
/// </summary>
public class CrossSurfaceParityTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;
    private readonly HttpClient _restClient;
    private readonly GrpcChannel _grpcChannel;
    private readonly PolicyService.PolicyServiceClient _grpcClient;

    public CrossSurfaceParityTests(PoliciesApiFactory factory)
    {
        _factory = factory;
        _restClient = factory.CreateClient();
        _grpcChannel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler(),
        });
        _grpcClient = new PolicyService.PolicyServiceClient(_grpcChannel);
    }

    private async Task<PolicyDto> SeedPolicyAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPolicyService>();
        var version = await service.CreateDraftAsync(new CreatePolicyRequest(
            Name: name,
            Description: "parity-fixture",
            Summary: "parity-fixture-summary",
            Enforcement: "Must",
            Severity: "Critical",
            Scopes: new[] { "prod" },
            RulesJson: "{}"), "parity-test");
        var policy = await service.GetPolicyAsync(version.PolicyId);
        return policy!;
    }

    [Fact]
    public async Task GetPolicy_RestVsGrpc_ReturnEquivalentDtos()
    {
        var seeded = await SeedPolicyAsync($"parity-grpc-{Guid.NewGuid():N}");

        var rest = await _restClient.GetFromJsonAsync<PolicyDto>($"/api/policies/{seeded.Id}");
        var grpc = await _grpcClient.GetPolicyAsync(new GetPolicyRequest { Id = seeded.Id.ToString() });

        rest.Should().NotBeNull();
        grpc.Policy.Id.Should().Be(rest!.Id.ToString());
        grpc.Policy.Name.Should().Be(rest.Name);
        grpc.Policy.Description.Should().Be(rest.Description);
        grpc.Policy.VersionCount.Should().Be(rest.VersionCount);
        grpc.Policy.CreatedBySubjectId.Should().Be(rest.CreatedBySubjectId);
    }

    [Fact]
    public async Task GetPolicy_RestVsMcp_DescribeSamePolicy()
    {
        var seeded = await SeedPolicyAsync($"parity-mcp-{Guid.NewGuid():N}");

        var rest = await _restClient.GetFromJsonAsync<PolicyDto>($"/api/policies/{seeded.Id}");

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPolicyService>();
        var mcp = await PolicyTools.GetPolicy(service, seeded.Id.ToString());

        rest.Should().NotBeNull();
        mcp.Should().Contain(rest!.Name);
        mcp.Should().Contain(rest.Id.ToString());
    }

    [Fact]
    public async Task ListVersions_RestVsGrpc_ReturnEquivalentVersionMetadata()
    {
        var seeded = await SeedPolicyAsync($"parity-versions-{Guid.NewGuid():N}");

        var rest = await _restClient.GetFromJsonAsync<List<PolicyVersionDto>>(
            $"/api/policies/{seeded.Id}/versions");
        var grpc = await _grpcClient.ListVersionsAsync(new ListVersionsRequest
        {
            PolicyId = seeded.Id.ToString(),
        });

        rest.Should().NotBeNull();
        rest!.Should().HaveCount(grpc.Versions.Count);
        var restFirst = rest![0];
        var grpcFirst = grpc.Versions[0];
        grpcFirst.Id.Should().Be(restFirst.Id.ToString());
        grpcFirst.Version.Should().Be(restFirst.Version);
        grpcFirst.State.Should().Be(restFirst.State);
        grpcFirst.Enforcement.Should().Be(restFirst.Enforcement);
        grpcFirst.Severity.Should().Be(restFirst.Severity);
        grpcFirst.Scopes.Should().BeEquivalentTo(restFirst.Scopes);
        grpcFirst.Summary.Should().Be(restFirst.Summary);
    }

    [Fact]
    public async Task GetActiveVersion_DraftOnly_AllSurfacesReturnNotFound()
    {
        // No publish yet — REST returns 404, gRPC raises NOT_FOUND, MCP returns
        // a "no active version" message. Same semantic across surfaces.
        var seeded = await SeedPolicyAsync($"parity-active-{Guid.NewGuid():N}");

        var rest = await _restClient.GetAsync($"/api/policies/{seeded.Id}/versions/active");
        rest.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);

        var grpcCall = async () => await _grpcClient.GetActiveVersionAsync(new GetActiveVersionRequest
        {
            PolicyId = seeded.Id.ToString(),
        });
        await grpcCall.Should().ThrowAsync<Grpc.Core.RpcException>()
            .Where(e => e.StatusCode == Grpc.Core.StatusCode.NotFound);

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPolicyService>();
        var mcp = await PolicyTools.GetActiveVersion(service, seeded.Id.ToString());
        mcp.Should().Contain("no active version");
    }
}
