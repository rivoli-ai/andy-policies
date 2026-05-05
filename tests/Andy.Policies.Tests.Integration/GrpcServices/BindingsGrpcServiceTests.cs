// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using ProtoBindStrength = Andy.Policies.Api.Protos.BindStrength;

namespace Andy.Policies.Tests.Integration.GrpcServices;

/// <summary>
/// End-to-end gRPC tests for the binding surface (P3.6, story
/// rivoli-ai/andy-policies#24). Exercises every RPC over a real HTTP/2
/// channel against the test server, verifying the proto contract, the
/// generated stubs, exception → status-code mapping, and parity with
/// the REST/MCP surfaces (retired-version refusal, soft-delete,
/// dedup/order on resolve).
/// </summary>
public class BindingsGrpcServiceTests : IClassFixture<PoliciesApiFactory>, IDisposable
{
    private readonly PoliciesApiFactory _factory;
    private readonly GrpcChannel _channel;
    private readonly Andy.Policies.Api.Protos.BindingService.BindingServiceClient _bindings;
    private readonly Andy.Policies.Api.Protos.PolicyService.PolicyServiceClient _policies;
    private readonly LifecycleService.LifecycleServiceClient _lifecycle;

    public BindingsGrpcServiceTests(PoliciesApiFactory factory)
    {
        _factory = factory;
        var handler = factory.Server.CreateHandler();
        _channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
        _bindings = new Andy.Policies.Api.Protos.BindingService.BindingServiceClient(_channel);
        _policies = new Andy.Policies.Api.Protos.PolicyService.PolicyServiceClient(_channel);
        _lifecycle = new LifecycleService.LifecycleServiceClient(_channel);
    }

    public void Dispose() => _channel.Dispose();

    private static CreateDraftRequest MinimalCreate(string name) => new()
    {
        Name = name,
        Description = "",
        Summary = "summary",
        Enforcement = "Must",
        Severity = "Critical",
        RulesJson = "{}",
    };

    private async Task<PolicyVersionMessage> CreateDraftAsync(string slug)
    {
        // P7.3 (#55): pin the proposer subject to "test-creator" so the
        // default test subject "test-user" can act as the publisher
        // without tripping the publish-time self-approval guard.
        var metadata = new Metadata { { TestAuthHandler.SubjectHeader, "test-creator" } };
        var draft = await _policies.CreateDraftAsync(MinimalCreate(slug), metadata);
        return draft.Version;
    }

    private async Task<PolicyVersionMessage> CreateAndPublishAsync(string slug)
    {
        var draft = await CreateDraftAsync(slug);
        var publish = await _lifecycle.PublishVersionAsync(new PublishVersionRequest
        {
            PolicyId = draft.PolicyId,
            VersionId = draft.Id,
            Rationale = "ship",
        });
        return publish.Version;
    }

    private static string Slug(string prefix) =>
        $"grpc-{prefix}-{Guid.NewGuid():N}".Substring(0, 16);

    [Fact]
    public async Task CreateBinding_OnDraftVersion_ReturnsPopulatedBinding()
    {
        var draft = await CreateDraftAsync(Slug("bind"));

        var response = await _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
        {
            PolicyVersionId = draft.Id,
            TargetType = TargetType.Repo,
            TargetRef = "repo:rivoli-ai/policy-x",
            BindStrength = ProtoBindStrength.Mandatory,
        });

        response.Binding.PolicyVersionId.Should().Be(draft.Id);
        response.Binding.TargetType.Should().Be(TargetType.Repo);
        response.Binding.TargetRef.Should().Be("repo:rivoli-ai/policy-x");
        response.Binding.BindStrength.Should().Be(ProtoBindStrength.Mandatory);
        response.Binding.DeletedAt.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateBinding_OnRetiredVersion_ThrowsFailedPrecondition()
    {
        var version = await CreateAndPublishAsync(Slug("retired"));
        await _lifecycle.TransitionVersionAsync(new TransitionVersionRequest
        {
            PolicyId = version.PolicyId,
            VersionId = version.Id,
            TargetState = "Retired",
            Rationale = "recall",
        });

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
            {
                PolicyVersionId = version.Id,
                TargetType = TargetType.Repo,
                TargetRef = "repo:any/repo",
                BindStrength = ProtoBindStrength.Recommended,
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task CreateBinding_WithUnspecifiedTargetType_ThrowsInvalidArgument()
    {
        var draft = await CreateDraftAsync(Slug("unsp"));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
            {
                PolicyVersionId = draft.Id,
                TargetType = TargetType.Unspecified,
                TargetRef = "repo:a/b",
                BindStrength = ProtoBindStrength.Mandatory,
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task CreateBinding_WithEmptyTargetRef_ThrowsInvalidArgument()
    {
        var draft = await CreateDraftAsync(Slug("empty"));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
            {
                PolicyVersionId = draft.Id,
                TargetType = TargetType.Repo,
                TargetRef = "  ",
                BindStrength = ProtoBindStrength.Mandatory,
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task CreateBinding_WithUnknownVersion_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
            {
                PolicyVersionId = Guid.NewGuid().ToString(),
                TargetType = TargetType.Repo,
                TargetRef = "repo:a/b",
                BindStrength = ProtoBindStrength.Mandatory,
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task GetBinding_RoundTripsAfterCreate()
    {
        var draft = await CreateDraftAsync(Slug("get"));
        var created = await _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
        {
            PolicyVersionId = draft.Id,
            TargetType = TargetType.Tenant,
            TargetRef = $"tenant:{Guid.NewGuid()}",
            BindStrength = ProtoBindStrength.Recommended,
        });

        var fetched = await _bindings.GetBindingAsync(new GetBindingRequest { Id = created.Binding.Id });

        fetched.Binding.Id.Should().Be(created.Binding.Id);
        fetched.Binding.TargetType.Should().Be(TargetType.Tenant);
    }

    [Fact]
    public async Task GetBinding_OnUnknownId_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _bindings.GetBindingAsync(new GetBindingRequest { Id = Guid.NewGuid().ToString() }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteBinding_RoundTrips_AndSecondDeleteThrowsNotFound()
    {
        var draft = await CreateDraftAsync(Slug("del"));
        var created = await _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
        {
            PolicyVersionId = draft.Id,
            TargetType = TargetType.Repo,
            TargetRef = "repo:a/del",
            BindStrength = ProtoBindStrength.Mandatory,
        });

        await _bindings.DeleteBindingAsync(new DeleteBindingRequest
        {
            Id = created.Binding.Id,
            Rationale = "no longer needed",
        });

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _bindings.DeleteBindingAsync(new DeleteBindingRequest { Id = created.Binding.Id }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task ListBindingsByPolicyVersion_HonoursIncludeDeletedFlag()
    {
        var draft = await CreateDraftAsync(Slug("list"));
        await _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
        {
            PolicyVersionId = draft.Id,
            TargetType = TargetType.Repo,
            TargetRef = "repo:a/alive",
            BindStrength = ProtoBindStrength.Mandatory,
        });
        var dead = await _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
        {
            PolicyVersionId = draft.Id,
            TargetType = TargetType.Repo,
            TargetRef = "repo:a/dead",
            BindStrength = ProtoBindStrength.Mandatory,
        });
        await _bindings.DeleteBindingAsync(new DeleteBindingRequest { Id = dead.Binding.Id });

        var visible = await _bindings.ListBindingsByPolicyVersionAsync(new ListBindingsByPolicyVersionRequest
        {
            PolicyVersionId = draft.Id,
            IncludeDeleted = false,
        });
        var all = await _bindings.ListBindingsByPolicyVersionAsync(new ListBindingsByPolicyVersionRequest
        {
            PolicyVersionId = draft.Id,
            IncludeDeleted = true,
        });

        visible.Bindings.Should().ContainSingle();
        all.Bindings.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListBindingsByTarget_ReturnsExactMatchOnly()
    {
        var draft = await CreateDraftAsync(Slug("tgt"));
        var lower = $"repo:rivoli-ai/q-{Guid.NewGuid():N}".Substring(0, 30);
        var upper = lower.ToUpperInvariant();
        await _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
        {
            PolicyVersionId = draft.Id,
            TargetType = TargetType.Repo,
            TargetRef = lower,
            BindStrength = ProtoBindStrength.Mandatory,
        });
        await _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
        {
            PolicyVersionId = draft.Id,
            TargetType = TargetType.Repo,
            TargetRef = upper,
            BindStrength = ProtoBindStrength.Mandatory,
        });

        var lowerResp = await _bindings.ListBindingsByTargetAsync(new ListBindingsByTargetRequest
        {
            TargetType = TargetType.Repo,
            TargetRef = lower,
        });
        var upperResp = await _bindings.ListBindingsByTargetAsync(new ListBindingsByTargetRequest
        {
            TargetType = TargetType.Repo,
            TargetRef = upper,
        });

        lowerResp.Bindings.Should().ContainSingle().Which.TargetRef.Should().Be(lower);
        upperResp.Bindings.Should().ContainSingle().Which.TargetRef.Should().Be(upper);
    }

    [Fact]
    public async Task ResolveBindings_MatchesRestSurface()
    {
        var version = await CreateAndPublishAsync(Slug("res"));
        var target = $"template:{Guid.NewGuid()}";
        await _bindings.CreateBindingAsync(new Andy.Policies.Api.Protos.CreateBindingRequest
        {
            PolicyVersionId = version.Id,
            TargetType = TargetType.Template,
            TargetRef = target,
            BindStrength = ProtoBindStrength.Mandatory,
        });

        var grpc = await _bindings.ResolveBindingsAsync(new ResolveBindingsRequest
        {
            TargetType = TargetType.Template,
            TargetRef = target,
        });

        // REST parity check: same target id resolves to the same binding set.
        var http = _factory.CreateClient();
        var rest = await http.GetFromJsonAsync<Andy.Policies.Application.Dtos.ResolveBindingsResponse>(
            $"/api/bindings/resolve?targetType=Template&targetRef={Uri.EscapeDataString(target)}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });

        grpc.Count.Should().Be(rest!.Count);
        grpc.Bindings.Select(b => b.BindingId)
            .Should().BeEquivalentTo(rest.Bindings.Select(b => b.BindingId.ToString()));
    }

    [Fact]
    public async Task ResolveBindings_OnEmptyTarget_ReturnsZeroCount()
    {
        var response = await _bindings.ResolveBindingsAsync(new ResolveBindingsRequest
        {
            TargetType = TargetType.Repo,
            TargetRef = $"repo:none/missing-{Guid.NewGuid():N}",
        });

        response.Count.Should().Be(0);
        response.Bindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveBindings_WithEmptyTargetRef_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _bindings.ResolveBindingsAsync(new ResolveBindingsRequest
            {
                TargetType = TargetType.Repo,
                TargetRef = "",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
