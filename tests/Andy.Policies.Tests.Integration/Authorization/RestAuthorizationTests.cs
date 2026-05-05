// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Tests.Integration.Controllers;
using Andy.Policies.Tests.Integration.Fixtures;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Authorization;

/// <summary>
/// P7.5 (#61) — end-to-end REST authorization tests against a
/// <see cref="RbacStubFixture"/>-backed andy-rbac. Proves the full
/// pipeline (<c>[Authorize(Policy=...)]</c> →
/// <c>RbacAuthorizationHandler</c> → <c>HttpRbacChecker</c> → wire
/// body → andy-rbac) under deterministic stub responses, including
/// the fail-closed posture on outage.
/// </summary>
public sealed class RestAuthorizationTests : IClassFixture<RbacStubFixture>, IDisposable
{
    private readonly RbacStubFixture _rbac;
    private readonly RbacTestApplicationFactory _factory;
    private readonly HttpClient _client;

    public RestAuthorizationTests(RbacStubFixture rbac)
    {
        _rbac = rbac;
        _rbac.Reset();
        _factory = new RbacTestApplicationFactory(rbac);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static CreatePolicyRequest MinimalCreate(string slug) => new(
        Name: slug,
        Description: null,
        Summary: "summary",
        Enforcement: "Must",
        Severity: "Critical",
        Scopes: Array.Empty<string>(),
        RulesJson: "{}");

    [Fact]
    public async Task ListPolicies_RbacAllow_Returns200()
    {
        _rbac.Allow("user:reader", "andy-policies:policy:read");

        var resp = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/policies")
            {
                Headers = { { TestAuthHandler.SubjectHeader, "user:reader" } },
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var calls = _rbac.Received().Where(c => c.SubjectId == "user:reader").ToList();
        calls.Should().NotBeEmpty();
        calls[0].Permission.Should().Be("andy-policies:policy:read");
        // The list endpoint has no route id → resource instance is null.
        calls[0].ResourceInstanceId.Should().BeNull();
    }

    [Fact]
    public async Task CreatePolicy_DefaultDeny_Returns403()
    {
        // No Allow installed — default-deny catch-all triggers.
        var resp = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/policies")
            {
                Headers = { { TestAuthHandler.SubjectHeader, "user:nobody" } },
                Content = JsonContent.Create(MinimalCreate("denied-by-default")),
            });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var calls = _rbac.Received().Where(c => c.SubjectId == "user:nobody").ToList();
        calls.Should().NotBeEmpty();
        calls[0].Permission.Should().Be("andy-policies:policy:author");
    }

    [Fact]
    public async Task CreatePolicy_AndyRbacOutage_FailsClosedTo403()
    {
        _rbac.SimulateOutage();

        var resp = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/policies")
            {
                Headers = { { TestAuthHandler.SubjectHeader, "user:fail-closed" } },
                Content = JsonContent.Create(MinimalCreate("outage")),
            });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetPolicy_OutgoingPayloadCarriesRouteResourceInstance()
    {
        _rbac.Allow("user:reader", "andy-policies:policy:read");

        // The id need not exist — Authorize fires before the controller
        // body, so we get to assert the captured payload regardless of
        // what the controller would do next (404 here).
        var versionId = "11111111-1111-1111-1111-111111111111";
        await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/policies/{versionId}")
            {
                Headers = { { TestAuthHandler.SubjectHeader, "user:reader" } },
            });

        var calls = _rbac.Received()
            .Where(c => c.SubjectId == "user:reader" && c.ResourceInstanceId is not null)
            .ToList();
        calls.Should().NotBeEmpty();
        calls[0].Permission.Should().Be("andy-policies:policy:read");
        calls[0].ResourceInstanceId.Should().Be($"policy:{versionId}");
    }
}
