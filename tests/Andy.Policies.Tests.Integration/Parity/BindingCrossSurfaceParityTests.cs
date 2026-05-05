// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Api.Mcp;
using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ProtoBindStrength = Andy.Policies.Api.Protos.BindStrength;
using DomainBindStrength = Andy.Policies.Domain.Enums.BindStrength;
using RestResolveBindingsResponse = Andy.Policies.Application.Dtos.ResolveBindingsResponse;

namespace Andy.Policies.Tests.Integration.Parity;

/// <summary>
/// P3.8 (#26) cross-surface parity sweep for the binding catalog. The
/// REST (P3.3, P3.4), MCP (P3.5), and gRPC (P3.6) surfaces all delegate
/// to the same <see cref="IBindingService"/> + <see cref="IBindingResolver"/>;
/// this fixture proves their wire serializers don't drift by issuing the
/// same logical request across surfaces and asserting the response set
/// is identical (count, binding ids, and dimension fields).
///
/// The CLI (P3.7) is a thin REST client — its parity is implied by the
/// REST assertion (mirrors the rationale in
/// <see cref="CrossSurfaceParityTests"/>).
/// </summary>
public class BindingCrossSurfaceParityTests : IClassFixture<PoliciesApiFactory>, IDisposable
{
    private readonly PoliciesApiFactory _factory;
    private readonly HttpClient _restClient;
    private readonly GrpcChannel _grpcChannel;
    private readonly Andy.Policies.Api.Protos.BindingService.BindingServiceClient _grpcBindings;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public BindingCrossSurfaceParityTests(PoliciesApiFactory factory)
    {
        _factory = factory;
        _restClient = factory.CreateClient();
        _grpcChannel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler(),
        });
        _grpcBindings = new Andy.Policies.Api.Protos.BindingService.BindingServiceClient(_grpcChannel);
    }

    public void Dispose() => _grpcChannel.Dispose();

    private static string Slug(string prefix) =>
        $"parity-{prefix}-{Guid.NewGuid():N}".Substring(0, 16);

    private async Task<PolicyVersionDto> SeedActiveVersionAsync(string slug)
    {
        // Use REST end-to-end — covers Program.cs DI graph, lifecycle service,
        // serializers, the works.
        var draft = await _restClient.PostAsJsonAsync("/api/policies", new
        {
            name = slug,
            description = (string?)null,
            summary = "parity-fixture",
            enforcement = "Must",
            severity = "Critical",
            scopes = Array.Empty<string>(),
            rulesJson = "{}",
        });
        draft.EnsureSuccessStatusCode();
        var version = (await draft.Content.ReadFromJsonAsync<PolicyVersionDto>())!;

        var publish = await _restClient.PostAsJsonAsApproverAsync(
            $"/api/policies/{version.PolicyId}/versions/{version.Id}/publish",
            new LifecycleTransitionRequest("parity"));
        publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
    }

    [Fact]
    public async Task Resolve_ReturnsIdenticalSet_AcrossRestAndGrpc()
    {
        var version = await SeedActiveVersionAsync(Slug("res"));
        var target = $"template:{Guid.NewGuid()}";

        // Seed two bindings against the same target+version: Mandatory wins
        // dedup; the resolver should return exactly one row.
        await _restClient.PostAsJsonAsync("/api/bindings", new
        {
            policyVersionId = version.Id,
            targetType = "Template",
            targetRef = target,
            bindStrength = "Recommended",
        });
        await _restClient.PostAsJsonAsync("/api/bindings", new
        {
            policyVersionId = version.Id,
            targetType = "Template",
            targetRef = target,
            bindStrength = "Mandatory",
        });

        // REST.
        var rest = await _restClient.GetFromJsonAsync<RestResolveBindingsResponse>(
            $"/api/bindings/resolve?targetType=Template&targetRef={Uri.EscapeDataString(target)}",
            JsonOptions);
        rest.Should().NotBeNull();

        // gRPC.
        var grpc = await _grpcBindings.ResolveBindingsAsync(new ResolveBindingsRequest
        {
            TargetType = TargetType.Template,
            TargetRef = target,
        });

        // Counts match.
        rest!.Count.Should().Be(grpc.Count);

        // Binding ids — sorted to compare regardless of natural ordering.
        var restIds = rest.Bindings.Select(b => b.BindingId.ToString()).OrderBy(x => x).ToList();
        var grpcIds = grpc.Bindings.Select(b => b.BindingId).OrderBy(x => x).ToList();
        restIds.Should().Equal(grpcIds);

        // Wire-format casing: both surfaces emit ADR 0001 §6 strings.
        var restFirst = rest.Bindings.Single();
        var grpcFirst = grpc.Bindings.Single();
        restFirst.Enforcement.Should().Be(grpcFirst.Enforcement).And.Be("MUST");
        restFirst.Severity.Should().Be(grpcFirst.Severity).And.Be("critical");
        restFirst.VersionState.Should().Be(grpcFirst.VersionState).And.Be("Active");
        restFirst.BindStrength.Should().Be(DomainBindStrength.Mandatory);
        grpcFirst.BindStrength.Should().Be(ProtoBindStrength.Mandatory);
    }

    [Fact]
    public async Task Resolve_ReturnsIdenticalSet_AcrossRestAndMcpTool()
    {
        var version = await SeedActiveVersionAsync(Slug("mcp"));
        var target = $"repo:rivoli-ai/parity-{Guid.NewGuid():N}".Substring(0, 40);
        await _restClient.PostAsJsonAsync("/api/bindings", new
        {
            policyVersionId = version.Id,
            targetType = "Repo",
            targetRef = target,
            bindStrength = "Mandatory",
        });

        // REST.
        var rest = await _restClient.GetFromJsonAsync<RestResolveBindingsResponse>(
            $"/api/bindings/resolve?targetType=Repo&targetRef={Uri.EscapeDataString(target)}",
            JsonOptions);

        // MCP — invoke the tool method directly with the same DI graph the
        // MCP server would resolve. The tool serializes its own JSON envelope;
        // we deserialize and compare structurally to the REST shape.
        using var scope = _factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IBindingResolver>();
        var mcpJson = await BindingTools.Resolve(resolver, "Repo", target);
        using var mcpDoc = JsonDocument.Parse(mcpJson);

        // Counts match.
        rest!.Count.Should().Be(mcpDoc.RootElement.GetProperty("count").GetInt32());

        // Binding ids — sorted to compare regardless of natural ordering.
        var restIds = rest.Bindings.Select(b => b.BindingId.ToString()).OrderBy(x => x).ToList();
        var mcpIds = mcpDoc.RootElement.GetProperty("bindings").EnumerateArray()
            .Select(b => b.GetProperty("bindingId").GetString()!)
            .OrderBy(x => x)
            .ToList();
        restIds.Should().Equal(mcpIds);
    }

    [Fact]
    public async Task RetiredVersionRefusal_HasParityAcrossSurfaces()
    {
        // Seed a Retired version on a fresh policy.
        var version = await SeedActiveVersionAsync(Slug("ret"));
        var retire = await _restClient.PostAsJsonAsync(
            $"/api/policies/{version.PolicyId}/versions/{version.Id}/retire",
            new LifecycleTransitionRequest("parity-recall"));
        retire.EnsureSuccessStatusCode();

        // REST: 409.
        var rest = await _restClient.PostAsJsonAsync("/api/bindings", new
        {
            policyVersionId = version.Id,
            targetType = "Template",
            targetRef = "template:retired-target",
            bindStrength = "Mandatory",
        });
        rest.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);

        // gRPC: FailedPrecondition.
        var grpcEx = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() =>
            _grpcBindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
            {
                PolicyVersionId = version.Id.ToString(),
                TargetType = TargetType.Template,
                TargetRef = "template:retired-target",
                BindStrength = ProtoBindStrength.Mandatory,
            }).ResponseAsync);
        grpcEx.StatusCode.Should().Be(Grpc.Core.StatusCode.FailedPrecondition);

        // MCP: policy.binding.retired_target.
        using var scope = _factory.Services.CreateScope();
        var bindingService = scope.ServiceProvider.GetRequiredService<IBindingService>();
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "parity-actor"),
            }, authenticationType: "Test")),
        };
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        var mcpOutput = await BindingTools.Create(
            bindingService, accessor,
            version.Id.ToString(), "Template", "template:retired-target", "Mandatory");
        mcpOutput.Should().StartWith("policy.binding.retired_target:");
    }
}
