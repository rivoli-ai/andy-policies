// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Infrastructure.Services.Rbac;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Infrastructure.Rbac;

/// <summary>
/// P7.2 (#51) — covers <see cref="HttpRbacChecker"/>'s allow / deny /
/// fail-closed paths, the 60s cache contract, and resource-instance
/// cache scoping.
/// </summary>
public class HttpRbacCheckerTests
{
    private static HttpRbacChecker BuildChecker(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://rbac.test") };
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new HttpRbacChecker(http, cache, NullLogger<HttpRbacChecker>.Instance);
    }

    [Fact]
    public async Task AllowResponse_IsReturnedAndCachedAcrossCalls()
    {
        var handler = new StubHandler(_ =>
            JsonResponse(HttpStatusCode.OK, new { Allowed = true, Reason = "role:approver" }));
        var checker = BuildChecker(handler);

        var first = await checker.CheckAsync("user:alice", "andy-policies:policy:publish",
            Array.Empty<string>(), null, CancellationToken.None);
        var second = await checker.CheckAsync("user:alice", "andy-policies:policy:publish",
            Array.Empty<string>(), null, CancellationToken.None);

        first.Allowed.Should().BeTrue();
        first.Reason.Should().Be("role:approver");
        second.Should().Be(first);
        handler.CallCount.Should().Be(1, "second call must be served from cache");
    }

    [Fact]
    public async Task DenyResponse_IsReturnedAndCachedSeparately()
    {
        var handler = new StubHandler(_ =>
            JsonResponse(HttpStatusCode.OK, new { Allowed = false, Reason = "no-permission" }));
        var checker = BuildChecker(handler);

        var first = await checker.CheckAsync("user:bob", "andy-policies:policy:publish",
            Array.Empty<string>(), null, CancellationToken.None);
        var second = await checker.CheckAsync("user:bob", "andy-policies:policy:publish",
            Array.Empty<string>(), null, CancellationToken.None);

        first.Allowed.Should().BeFalse();
        first.Reason.Should().Be("no-permission");
        handler.CallCount.Should().Be(1, "deny is cached just like allow");
        second.Should().Be(first);
    }

    [Fact]
    public async Task TransportError_FailsClosedAndIsNotCached()
    {
        var handler = new StubHandler(_ =>
            throw new HttpRequestException("connection refused"));
        var checker = BuildChecker(handler);

        var first = await checker.CheckAsync("user:carol", "andy-policies:policy:publish",
            Array.Empty<string>(), null, CancellationToken.None);
        var second = await checker.CheckAsync("user:carol", "andy-policies:policy:publish",
            Array.Empty<string>(), null, CancellationToken.None);

        first.Allowed.Should().BeFalse();
        first.Reason.Should().StartWith("rbac-unreachable");
        handler.CallCount.Should().Be(2,
            "fail-closed must not be cached so a recovered andy-rbac is picked up immediately");
        second.Should().Be(first);
    }

    [Fact]
    public async Task TimeoutOnHttpClient_FailsClosed()
    {
        // Simulate a timeout: HttpClient throws OperationCanceledException
        // with a non-cancelled outer token. HttpRbacChecker must
        // distinguish caller-cancel from internal timeout.
        var handler = new StubHandler(_ =>
            throw new OperationCanceledException("timed out"));
        var checker = BuildChecker(handler);

        var decision = await checker.CheckAsync("user:dave", "andy-policies:policy:publish",
            Array.Empty<string>(), null, CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().StartWith("rbac-unreachable");
    }

    [Fact]
    public async Task NonSuccessStatus_FailsClosedAndIsNotCached()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var checker = BuildChecker(handler);

        var first = await checker.CheckAsync("user:eve", "andy-policies:policy:publish",
            Array.Empty<string>(), null, CancellationToken.None);
        var second = await checker.CheckAsync("user:eve", "andy-policies:policy:publish",
            Array.Empty<string>(), null, CancellationToken.None);

        first.Allowed.Should().BeFalse();
        first.Reason.Should().StartWith("rbac-unreachable");
        handler.CallCount.Should().Be(2, "non-2xx is treated as transport error and not cached");
    }

    [Fact]
    public async Task ResourceInstanceId_ScopesTheCacheKey()
    {
        var handler = new StubHandler(_ =>
            JsonResponse(HttpStatusCode.OK, new { Allowed = true, Reason = "role:approver" }));
        var checker = BuildChecker(handler);

        await checker.CheckAsync("user:alice", "andy-policies:override:approve",
            Array.Empty<string>(), "scope:tenant-a", CancellationToken.None);
        await checker.CheckAsync("user:alice", "andy-policies:override:approve",
            Array.Empty<string>(), "scope:tenant-b", CancellationToken.None);
        await checker.CheckAsync("user:alice", "andy-policies:override:approve",
            Array.Empty<string>(), null, CancellationToken.None);

        handler.CallCount.Should().Be(3,
            "different resourceInstanceIds — including null — must hit andy-rbac independently");
    }

    [Fact]
    public async Task RequestBody_UsesExpectedPropertyNames()
    {
        string? captured = null;
        var handler = new StubHandler(req =>
        {
            captured = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(HttpStatusCode.OK, new { Allowed = true, Reason = "role:author" });
        });
        var checker = BuildChecker(handler);

        await checker.CheckAsync("user:alice", "andy-policies:policy:author",
            new[] { "team:authors", "team:eu" }, "scope:tenant-a", CancellationToken.None);

        captured.Should().NotBeNull();
        captured.Should().Contain("\"SubjectId\":\"user:alice\"");
        captured.Should().Contain("\"Permission\":\"andy-policies:policy:author\"");
        captured.Should().Contain("\"ResourceInstanceId\":\"scope:tenant-a\"");
        captured.Should().Contain("\"Groups\":[\"team:authors\",\"team:eu\"]");
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAndDoesNotFailClosed()
    {
        var handler = new StubHandler(_ => throw new OperationCanceledException());
        var checker = BuildChecker(handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Caller has cancelled; HttpRbacChecker must let the
        // OperationCanceledException propagate (it's the caller's
        // intent, not a timeout). The fail-closed branch only fires
        // when ct.IsCancellationRequested is false.
        Func<Task> act = () => checker.CheckAsync(
            "user:alice", "andy-policies:policy:publish",
            Array.Empty<string>(), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        var json = JsonSerializer.Serialize(body);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int CallCount { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            try
            {
                return Task.FromResult(_respond(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}
