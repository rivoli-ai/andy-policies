// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Protos;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Andy.Policies.Tests.Integration.GrpcServices;

/// <summary>
/// End-to-end gRPC tests for the lifecycle surface (P2.6, story
/// rivoli-ai/andy-policies#16). Exercises every RPC over a real HTTP/2
/// channel against the test server, verifying the proto contract,
/// generated stubs, exception → status-code mapping, and parity with the
/// REST/MCP surfaces (auto-supersede, rationale enforcement, the
/// four-edge state matrix).
/// </summary>
public class LifecycleGrpcServiceTests : IClassFixture<PoliciesApiFactory>, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly LifecycleService.LifecycleServiceClient _lifecycle;
    private readonly Andy.Policies.Api.Protos.PolicyService.PolicyServiceClient _policies;

    public LifecycleGrpcServiceTests(PoliciesApiFactory factory)
    {
        var handler = factory.Server.CreateHandler();
        _channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
        _lifecycle = new LifecycleService.LifecycleServiceClient(_channel);
        _policies = new Andy.Policies.Api.Protos.PolicyService.PolicyServiceClient(_channel);
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
        var draft = await _policies.CreateDraftAsync(MinimalCreate(slug));
        return draft.Version;
    }

    private static string Slug(string prefix) =>
        $"grpc-{prefix}-{Guid.NewGuid():N}".Substring(0, 16);

    [Fact]
    public async Task PublishVersion_OnDraft_ReturnsActiveVersion()
    {
        var draft = await CreateDraftAsync(Slug("pub"));

        var response = await _lifecycle.PublishVersionAsync(new PublishVersionRequest
        {
            PolicyId = draft.PolicyId,
            VersionId = draft.Id,
            Rationale = "promote v1",
        });

        response.Version.State.Should().Be("Active");
        response.Version.Id.Should().Be(draft.Id);
    }

    [Fact]
    public async Task PublishVersion_AutoSupersedesPreviousActive()
    {
        var v1 = await CreateDraftAsync(Slug("auto"));
        await _lifecycle.PublishVersionAsync(new PublishVersionRequest
        {
            PolicyId = v1.PolicyId,
            VersionId = v1.Id,
            Rationale = "v1-live",
        });

        // Bump v1 to mint v2 Draft under the same policy.
        var v2Resp = await _policies.BumpDraftAsync(new BumpDraftRequest
        {
            PolicyId = v1.PolicyId,
            SourceVersionId = v1.Id,
        });
        var v2 = v2Resp.Version;

        await _lifecycle.PublishVersionAsync(new PublishVersionRequest
        {
            PolicyId = v2.PolicyId,
            VersionId = v2.Id,
            Rationale = "v2-live",
        });

        var v1After = await _policies.GetVersionAsync(new GetVersionRequest
        {
            PolicyId = v1.PolicyId,
            VersionId = v1.Id,
        });
        v1After.Version.State.Should().Be("WindingDown");

        var active = await _policies.GetActiveVersionAsync(new GetActiveVersionRequest
        {
            PolicyId = v1.PolicyId,
        });
        active.Version.Id.Should().Be(v2.Id);
    }

    [Fact]
    public async Task PublishVersion_EmptyRationale_ThrowsInvalidArgument()
    {
        var draft = await CreateDraftAsync(Slug("nora"));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _lifecycle.PublishVersionAsync(new PublishVersionRequest
            {
                PolicyId = draft.PolicyId,
                VersionId = draft.Id,
                Rationale = "  ",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("Rationale");
    }

    [Fact]
    public async Task PublishVersion_UnknownIds_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _lifecycle.PublishVersionAsync(new PublishVersionRequest
            {
                PolicyId = Guid.NewGuid().ToString(),
                VersionId = Guid.NewGuid().ToString(),
                Rationale = "go",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task PublishVersion_InvalidGuid_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _lifecycle.PublishVersionAsync(new PublishVersionRequest
            {
                PolicyId = "not-a-guid",
                VersionId = Guid.NewGuid().ToString(),
                Rationale = "go",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("policy_id");
    }

    [Fact]
    public async Task TransitionVersion_DisallowedTransition_ThrowsFailedPrecondition()
    {
        // Draft -> Retired is not in the matrix.
        var draft = await CreateDraftAsync(Slug("badtran"));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _lifecycle.TransitionVersionAsync(new TransitionVersionRequest
            {
                PolicyId = draft.PolicyId,
                VersionId = draft.Id,
                TargetState = "Retired",
                Rationale = "skip",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task TransitionVersion_AcceptsCaseInsensitiveTargetState()
    {
        var draft = await CreateDraftAsync(Slug("case"));

        var response = await _lifecycle.TransitionVersionAsync(new TransitionVersionRequest
        {
            PolicyId = draft.PolicyId,
            VersionId = draft.Id,
            TargetState = "active",
            Rationale = "ship",
        });

        response.Version.State.Should().Be("Active");
    }

    [Fact]
    public async Task TransitionVersion_TargetDraft_ThrowsInvalidArgument()
    {
        var draft = await CreateDraftAsync(Slug("draft"));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _lifecycle.TransitionVersionAsync(new TransitionVersionRequest
            {
                PolicyId = draft.PolicyId,
                VersionId = draft.Id,
                TargetState = "Draft",
                Rationale = "no",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task TransitionVersion_UnknownTargetState_ThrowsInvalidArgument()
    {
        var draft = await CreateDraftAsync(Slug("unk"));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _lifecycle.TransitionVersionAsync(new TransitionVersionRequest
            {
                PolicyId = draft.PolicyId,
                VersionId = draft.Id,
                TargetState = "Unicorn",
                Rationale = "?",
            }).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task TransitionVersion_RetireFromWindingDown_StampsRetired()
    {
        var draft = await CreateDraftAsync(Slug("retire"));
        await _lifecycle.PublishVersionAsync(new PublishVersionRequest
        {
            PolicyId = draft.PolicyId,
            VersionId = draft.Id,
            Rationale = "live",
        });
        await _lifecycle.TransitionVersionAsync(new TransitionVersionRequest
        {
            PolicyId = draft.PolicyId,
            VersionId = draft.Id,
            TargetState = "WindingDown",
            Rationale = "sunset",
        });

        var response = await _lifecycle.TransitionVersionAsync(new TransitionVersionRequest
        {
            PolicyId = draft.PolicyId,
            VersionId = draft.Id,
            TargetState = "Retired",
            Rationale = "tomb",
        });

        response.Version.State.Should().Be("Retired");
    }

    [Fact]
    public async Task GetMatrix_ReturnsTheFourCanonicalRules()
    {
        var response = await _lifecycle.GetMatrixAsync(new GetMatrixRequest());

        response.Rules.Should().HaveCount(4);
        response.Rules.Select(r => (r.From, r.To, r.Name)).Should().BeEquivalentTo(new[]
        {
            ("Draft", "Active", "Publish"),
            ("Active", "WindingDown", "WindDown"),
            ("Active", "Retired", "Retire"),
            ("WindingDown", "Retired", "Retire"),
        });
    }
}
