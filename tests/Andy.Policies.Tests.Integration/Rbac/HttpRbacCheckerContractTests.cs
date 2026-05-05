// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Infrastructure.Services.Rbac;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Andy.Policies.Tests.Integration.Rbac;

/// <summary>
/// P7.2 (#51) — exercises <see cref="HttpRbacChecker"/> against a
/// WireMock-backed andy-rbac stub. Pins the wire format (request body
/// property names) and the fail-closed contract on 5xx responses.
/// The full container harness (real andy-rbac process) lands in
/// P7.5 (#61); this test stays cheap so CI can run it on every PR.
/// </summary>
public sealed class HttpRbacCheckerContractTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    private HttpRbacChecker BuildChecker()
    {
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new HttpRbacChecker(http, cache, NullLogger<HttpRbacChecker>.Instance);
    }

    [Fact]
    public async Task SuccessfulCheck_SendsPascalCaseFieldsToAndyRbac()
    {
        _server
            .Given(Request.Create().WithPath("/api/check").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"Allowed\":true,\"Reason\":\"role:approver\"}"));

        var checker = BuildChecker();

        var decision = await checker.CheckAsync(
            "user:alice",
            "andy-policies:policy:publish",
            new[] { "team:authors" },
            "scope:tenant-a",
            CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Be("role:approver");

        var capturedBody = _server.LogEntries.Single().RequestMessage.Body;
        capturedBody.Should().Contain("\"SubjectId\":\"user:alice\"");
        capturedBody.Should().Contain("\"Permission\":\"andy-policies:policy:publish\"");
        capturedBody.Should().Contain("\"ResourceInstanceId\":\"scope:tenant-a\"");
        capturedBody.Should().Contain("\"Groups\":[\"team:authors\"]");
    }

    [Fact]
    public async Task ServiceUnavailable_FailsClosedWithStructuredReason()
    {
        _server
            .Given(Request.Create().WithPath("/api/check").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.ServiceUnavailable));

        var checker = BuildChecker();

        var decision = await checker.CheckAsync(
            "user:alice",
            "andy-policies:policy:publish",
            Array.Empty<string>(),
            null,
            CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().StartWith("rbac-unreachable");
    }
}
