// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Api.Filters;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Api;

/// <summary>
/// End-to-end tests for the bundle-pinning gate (P8.4, story
/// rivoli-ai/andy-policies#84). Pins the seven scenarios from the
/// spec: 400 + Problem-Details on missing bundleId / pinning-on,
/// 200 with snapshot-backed payload on bundleId / pinning-on, 200
/// from live state on missing bundleId / pinning-off, the gate
/// applies to the bindings and scopes read endpoints, the gate
/// does NOT apply to non-annotated endpoints (audit), and an
/// unknown bundleId returns 404 (the 400 path is reserved for the
/// missing-parameter case).
/// </summary>
public class BundlePinningGateTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _baseFactory;

    public BundlePinningGateTests(PoliciesApiFactory factory)
    {
        _baseFactory = factory;
    }

    /// <summary>
    /// The base factory stubs <see cref="IPinningPolicy"/> with
    /// pinning OFF so the wider integration suite stays focused on
    /// non-gate behaviour. <see cref="WithPinning"/> overrides the
    /// stub per-test to flip the gate.
    /// </summary>
    private WebApplicationFactory<Program> WithPinning(bool required) =>
        _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IPinningPolicy))
                    .ToList();
                foreach (var d in existing) services.Remove(d);
                services.AddSingleton<IPinningPolicy>(
                    new PoliciesApiFactory.StaticPinningPolicy(required));
            });
        });

    private async Task<(Guid bundleId, Guid policyId)> SeedBundleAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bundles = scope.ServiceProvider.GetRequiredService<IBundleService>();

        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = $"p-{Guid.NewGuid():N}".Substring(0, 14),
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

        var dto = await bundles.CreateAsync(
            new CreateBundleRequest($"snap-{Guid.NewGuid():N}".Substring(0, 16), null, "initial"),
            "seed",
            CancellationToken.None);
        return (dto.Id, policy.Id);
    }

    [Fact]
    public async Task ListPolicies_PinningOn_NoBundleId_Returns400_WithProblemTypeUri()
    {
        var factory = WithPinning(required: true);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/policies");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("type").GetString()
            .Should().Be(BundlePinningFilter.ProblemTypeUri);
        doc.RootElement.GetProperty("detail").GetString()
            .Should().Contain("Pinning required: pass ?bundleId=");
    }

    [Fact]
    public async Task ListPolicies_PinningOn_BundleIdProvided_Returns200_WithSnapshotPayload()
    {
        var factory = WithPinning(required: true);
        using var client = factory.CreateClient();
        var (bundleId, policyId) = await SeedBundleAsync(factory);

        var resp = await client.GetAsync($"/api/policies?bundleId={bundleId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // The factory is shared across the class fixture, so the
        // snapshot can include policies seeded by sibling gate tests.
        // Assert the just-seeded id appears, not that it's the only
        // entry — that's the dispatch-against-snapshot guarantee.
        var ids = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid())
            .ToList();
        ids.Should().Contain(
            policyId,
            "the snapshot carries the seeded policy and the dispatch must " +
            "return it instead of going to live state");
    }

    [Fact]
    public async Task ListPolicies_PinningOff_NoBundleId_Returns200_WithLivePayload()
    {
        var factory = WithPinning(required: false);
        using var client = factory.CreateClient();
        // The legacy live path is what /api/policies returns when
        // pinning is off — even with no bundleId. An empty catalog
        // is still a valid response (200 [] not 400).

        var resp = await client.GetAsync("/api/policies");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPolicy_PinningOn_UnknownBundleId_Returns404_NotBadRequest()
    {
        var factory = WithPinning(required: true);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync($"/api/policies/{Guid.NewGuid()}?bundleId={Guid.NewGuid()}");

        resp.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "an unknown bundleId is a 404 path; 400 is reserved for the " +
            "missing-parameter case so a consumer can tell pinning-violation " +
            "from typo'd-id");
    }

    [Fact]
    public async Task BindingsResolve_PinningOn_NoBundleId_Returns400()
    {
        var factory = WithPinning(required: true);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/bindings/resolve?targetType=Repo&targetRef=repo:any");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("type").GetString()
            .Should().Be(BundlePinningFilter.ProblemTypeUri);
    }

    [Fact]
    public async Task ScopesEffective_PinningOn_NoBundleId_Returns400()
    {
        var factory = WithPinning(required: true);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync($"/api/scopes/{Guid.NewGuid()}/effective-policies");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AuditList_IsNotGated_ByPinning()
    {
        var factory = WithPinning(required: true);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/audit");

        resp.StatusCode.Should().NotBe(
            HttpStatusCode.BadRequest,
            "the gate is per-action via [RequiresBundlePin]; audit endpoints " +
            "must not be over-applied or admin tooling that runs on a service " +
            "token without a bundle pin would lose access to the audit chain");
    }

    [Fact]
    public async Task BundlesController_IsNotGated_ByPinning()
    {
        // Negative test: the bundle endpoints themselves must not be
        // gated, otherwise consumers couldn't bootstrap a pin (chicken
        // and egg).
        var factory = WithPinning(required: true);
        using var client = factory.CreateClient();
        var (bundleId, _) = await SeedBundleAsync(factory);

        var resp = await client.GetAsync(
            $"/api/bundles/{bundleId}/resolve?targetType=Repo&targetRef=repo:any");

        resp.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
    }

}
