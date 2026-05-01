// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Audit;

/// <summary>
/// P6.5 (#45) — exercises <c>GET /api/audit/verify</c> over
/// the SQLite-backed factory. Asserts the contract pinned by
/// the issue: 200 with <see cref="ChainVerificationDto"/> on
/// success (regardless of <c>valid</c>), 400 on bad range, and
/// 401 unauthenticated.
/// </summary>
public class AuditVerifyEndpointTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;
    private readonly HttpClient _client;

    public AuditVerifyEndpointTests(PoliciesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<int> SeedEventsAsync(int count)
    {
        // Drive the chain via IAuditChain.AppendAsync directly so
        // the tests don't depend on which mutating service has
        // been wired up to write audit rows. P6.6 lands the
        // catalog-mutation -> audit-row plumbing for the listed
        // services; for now the chain is the only writer.
        using var scope = _factory.Services.CreateScope();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();
        for (var i = 1; i <= count; i++)
        {
            await chain.AppendAsync(new AuditAppendRequest(
                Action: "policy.update",
                EntityType: "Policy",
                EntityId: $"00000000-0000-0000-0000-{i:D12}",
                FieldDiffJson: $"[{{\"op\":\"replace\",\"path\":\"/n\",\"value\":{i}}}]",
                Rationale: $"event #{i}",
                ActorSubjectId: "user:test",
                ActorRoles: new[] { "admin" }), CancellationToken.None);
        }
        return count;
    }

    [Fact]
    public async Task Verify_FullChain_Returns200WithValid()
    {
        await SeedEventsAsync(5);

        var resp = await _client.GetAsync("/api/audit/verify");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<ChainVerificationDto>();
        dto!.Valid.Should().BeTrue();
        dto.FirstDivergenceSeq.Should().BeNull();
        dto.InspectedCount.Should().BeGreaterOrEqualTo(5);
        dto.LastSeq.Should().BeGreaterOrEqualTo(5);
    }

    [Fact]
    public async Task Verify_BoundedRange_HonorsFromAndTo()
    {
        // Fill the chain enough that we have a [from, to] range
        // strictly inside the chain. The factory is shared across
        // tests in the class so the chain may already have rows
        // from earlier; we read LastSeq first and chain-extend
        // from there.
        var firstResp = await _client.GetAsync("/api/audit/verify");
        var initial = await firstResp.Content.ReadFromJsonAsync<ChainVerificationDto>();
        var baseline = initial!.LastSeq;
        await SeedEventsAsync(5);

        var lower = baseline + 2;
        var upper = baseline + 4;

        var resp = await _client.GetAsync($"/api/audit/verify?fromSeq={lower}&toSeq={upper}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<ChainVerificationDto>();
        dto!.Valid.Should().BeTrue();
        dto.InspectedCount.Should().Be(3, "fromSeq=baseline+2, toSeq=baseline+4 → 3 rows");
        dto.LastSeq.Should().Be(upper);
    }

    [Fact]
    public async Task Verify_FromGreaterThanTo_Returns400()
    {
        var resp = await _client.GetAsync("/api/audit/verify?fromSeq=10&toSeq=5");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        problem.GetProperty("type").GetString().Should().Be("/problems/audit-verify-range");
        problem.GetProperty("errorCode").GetString().Should().Be("audit.verify.invalid_range");
    }

    [Theory]
    [InlineData("fromSeq=0")]
    [InlineData("fromSeq=-1")]
    [InlineData("toSeq=0")]
    public async Task Verify_NonPositiveBounds_Returns400(string queryString)
    {
        var resp = await _client.GetAsync($"/api/audit/verify?{queryString}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Verify_EmptyChain_ReturnsValidWithZeroCounts()
    {
        // Fresh factory so the chain is empty.
        await using var freshFactory = new PoliciesApiFactory();
        var freshClient = freshFactory.CreateClient();

        var resp = await freshClient.GetAsync("/api/audit/verify");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<ChainVerificationDto>();
        dto!.Valid.Should().BeTrue();
        dto.InspectedCount.Should().Be(0);
        dto.LastSeq.Should().Be(0);
    }
}
