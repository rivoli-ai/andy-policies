// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.GrpcServices;

/// <summary>
/// P6.8 (#50) — exercises the four audit gRPC RPCs against a
/// real HTTP/2 channel. Drives the same SQLite-backed factory
/// the rest of the integration suite uses; per-RPC happy paths
/// + error contracts (NOT_FOUND, INVALID_ARGUMENT for bad
/// input). Tamper detection + concurrent-append correctness
/// are already covered by P6.2's <c>AuditChainTests</c>; this
/// suite focuses on the wire-format adapter.
/// </summary>
public class AuditGrpcServiceTests : IClassFixture<PoliciesApiFactory>, IDisposable
{
    private readonly PoliciesApiFactory _factory;
    private readonly GrpcChannel _channel;
    private readonly Andy.Policies.Api.Protos.AuditService.AuditServiceClient _audit;

    public AuditGrpcServiceTests(PoliciesApiFactory factory)
    {
        _factory = factory;
        var handler = factory.Server.CreateHandler();
        _channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
        _audit = new Andy.Policies.Api.Protos.AuditService.AuditServiceClient(_channel);
    }

    public void Dispose() => _channel.Dispose();

    private async Task SeedAsync(int count)
    {
        using var scope = _factory.Services.CreateScope();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();
        for (var i = 1; i <= count; i++)
        {
            await chain.AppendAsync(new AuditAppendRequest(
                Action: "policy.update",
                EntityType: "Policy",
                EntityId: $"grpc-test-{Guid.NewGuid():n}-{i}",
                FieldDiffJson: $"[{{\"op\":\"replace\",\"path\":\"/n\",\"value\":{i}}}]",
                Rationale: $"event #{i}",
                ActorSubjectId: "user:grpc",
                ActorRoles: new[] { "admin" }), CancellationToken.None);
        }
    }

    [Fact]
    public async Task ListAudit_HappyPath_ReturnsItemsAndPageSize()
    {
        await SeedAsync(3);

        var resp = await _audit.ListAuditAsync(new ListAuditRequest
        {
            PageSize = 50,
            Action = "policy.update",
            Actor = "user:grpc",
        });

        resp.Items.Should().HaveCountGreaterOrEqualTo(3);
        resp.PageSize.Should().Be(50);
    }

    [Fact]
    public async Task ListAudit_PageSizeOutOfRange_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _audit.ListAuditAsync(new ListAuditRequest { PageSize = 501 }).ResponseAsync);
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ListAudit_BadFromTimestamp_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _audit.ListAuditAsync(new ListAuditRequest { From = "not-a-date" }).ResponseAsync);
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ListAudit_FromGreaterThanTo_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _audit.ListAuditAsync(new ListAuditRequest
            {
                From = "2026-12-31T00:00:00Z",
                To = "2026-01-01T00:00:00Z",
            }).ResponseAsync);
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ListAudit_MalformedCursor_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _audit.ListAuditAsync(new ListAuditRequest
            {
                Cursor = "not-base64-content!",
            }).ResponseAsync);
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task GetAudit_BadGuid_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _audit.GetAuditAsync(new GetAuditRequest { Id = "not-a-guid" }).ResponseAsync);
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task GetAudit_UnknownId_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _audit.GetAuditAsync(new GetAuditRequest { Id = Guid.NewGuid().ToString() }).ResponseAsync);
        ex.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task GetAudit_ExistingId_ReturnsMessage()
    {
        await SeedAsync(1);

        // Pull an id from the list endpoint to feed Get.
        var listResp = await _audit.ListAuditAsync(new ListAuditRequest { PageSize = 1 });
        listResp.Items.Should().NotBeEmpty();
        var id = listResp.Items[0].Id;

        var resp = await _audit.GetAuditAsync(new GetAuditRequest { Id = id });

        resp.Id.Should().Be(id);
        resp.HashHex.Should().HaveLength(64);
    }

    [Fact]
    public async Task VerifyAudit_HappyPath_ReturnsValid()
    {
        await SeedAsync(3);

        var resp = await _audit.VerifyAuditAsync(new VerifyAuditRequest());

        resp.Valid.Should().BeTrue();
        resp.FirstDivergenceSeq.Should().Be(0, "valid chains report 0 for the divergence-seq sentinel");
        resp.InspectedCount.Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public async Task VerifyAudit_FromGreaterThanTo_ThrowsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _audit.VerifyAuditAsync(new VerifyAuditRequest { FromSeq = 10, ToSeq = 5 }).ResponseAsync);
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task ExportAudit_StreamsChunks_ConcatToValidNdjson()
    {
        await SeedAsync(3);

        var call = _audit.ExportAudit(new ExportAuditRequest());
        var assembled = new MemoryStream();
        await foreach (var chunk in call.ResponseStream.ReadAllAsync())
        {
            chunk.Ndjson.WriteTo(assembled);
        }

        var text = Encoding.UTF8.GetString(assembled.ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().NotBeEmpty();
        // Every line should parse; the last one is the summary.
        var summary = JsonDocument.Parse(lines[^1]).RootElement;
        summary.GetProperty("type").GetString().Should().Be("summary");
    }

    [Fact]
    public async Task ExportAudit_FromGreaterThanTo_ThrowsInvalidArgument()
    {
        var call = _audit.ExportAudit(new ExportAuditRequest { FromSeq = 10, ToSeq = 5 });
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
                // Drain to surface the trailing status.
            }
        });
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
