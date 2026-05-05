// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Andy.Policies.Tests.Integration.Fixtures;

/// <summary>
/// Shared <see cref="WireMockServer"/>-based stub for andy-rbac
/// (P7.5, story rivoli-ai/andy-policies#61). Lets integration tests
/// drive the real
/// <see cref="Andy.Policies.Infrastructure.Services.Rbac.HttpRbacChecker"/>
/// against deterministic responses without spinning a real
/// andy-rbac container.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default-deny posture.</b> The fixture installs a low-priority
/// catch-all that responds <c>{ Allowed: false, Reason: "default-deny" }</c>
/// for every <c>POST /api/check</c>. Tests must explicitly call
/// <see cref="Allow(string, string, string?)"/> to grant; this prevents
/// a permissive stub from masking a missing
/// <see cref="Microsoft.AspNetCore.Authorization.AuthorizeAttribute"/>.
/// </para>
/// <para>
/// <b>Priority.</b> WireMock.Net treats lower numbers as higher
/// priority; per-test allow / deny / outage rules go in at priority
/// 10, the default-deny sits at priority 1000.
/// </para>
/// <para>
/// <b>Lifecycle.</b> Used as <see cref="IClassFixture{TFixture}"/>
/// — one server per test class. Each test should call
/// <see cref="Reset"/> at the start to drop per-test rules without
/// disturbing the default-deny.
/// </para>
/// </remarks>
public sealed class RbacStubFixture : IAsyncLifetime
{
    private const int DefaultDenyPriority = 1000;
    private const int OverridePriority = 10;
    private const int OutagePriority = 1;

    public WireMockServer Server { get; private set; } = default!;

    public string BaseUrl => Server.Url!;

    public Task InitializeAsync()
    {
        Server = WireMockServer.Start();
        InstallDefaultDeny();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Server.Stop();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drop every per-test rule and re-install the default-deny.
    /// Call at the top of each <c>[Fact]</c> for a clean slate.
    /// </summary>
    public void Reset()
    {
        Server.Reset();
        InstallDefaultDeny();
    }

    public void Allow(string subjectId, string permission, string? resourceInstanceId = null)
        => Server
            .Given(MatchCheck(subjectId, permission, resourceInstanceId))
            .AtPriority(OverridePriority)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"Allowed\":true,\"Reason\":\"role:{permission}\"}}"));

    public void Deny(string subjectId, string permission, string? resourceInstanceId = null)
        => Server
            .Given(MatchCheck(subjectId, permission, resourceInstanceId))
            .AtPriority(OverridePriority)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"Allowed\":false,\"Reason\":\"no-permission\"}"));

    /// <summary>
    /// Respond <c>503 Service Unavailable</c> to every check, simulating
    /// an andy-rbac outage. The
    /// <see cref="Andy.Policies.Infrastructure.Services.Rbac.HttpRbacChecker"/>
    /// must then fail-closed (denying every gated request).
    /// </summary>
    public void SimulateOutage()
        => Server
            .Given(Request.Create().WithPath("/api/check").UsingPost())
            .AtPriority(OutagePriority)
            .RespondWith(Response.Create().WithStatusCode(503));

    /// <summary>
    /// Snapshot of every <c>POST /api/check</c> body the stub has seen.
    /// Use to assert the outgoing payload (PascalCase property names,
    /// per-endpoint permission code, route-derived resource instance).
    /// </summary>
    public IReadOnlyList<ReceivedCheck> Received()
        => Server.LogEntries
            .Where(e => e.RequestMessage.Path == "/api/check"
                        && string.Equals(e.RequestMessage.Method, "POST", StringComparison.OrdinalIgnoreCase))
            .Select(e => ReceivedCheck.From(e.RequestMessage.Body))
            .ToList();

    private void InstallDefaultDeny()
        => Server
            .Given(Request.Create().WithPath("/api/check").UsingPost())
            .AtPriority(DefaultDenyPriority)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"Allowed\":false,\"Reason\":\"default-deny\"}"));

    private static IRequestBuilder MatchCheck(string subjectId, string permission, string? instance)
    {
        // Substring matching beats JSON-partial matching here: the
        // JsonPartialMatcher is finicky about case, null handling, and
        // anonymous-object → JObject quirks. The body is small and
        // deterministically PascalCased (HttpRbacChecker pins it), so
        // a single regex with the three fields ANDed via lookahead is
        // robust and self-documenting.
        var subPart = $"\"SubjectId\":\"{System.Text.RegularExpressions.Regex.Escape(subjectId)}\"";
        var permPart = $"\"Permission\":\"{System.Text.RegularExpressions.Regex.Escape(permission)}\"";
        var pattern = $"^(?=.*{subPart})(?=.*{permPart})";
        if (instance is not null)
        {
            var instPart = $"\"ResourceInstanceId\":\"{System.Text.RegularExpressions.Regex.Escape(instance)}\"";
            pattern += $"(?=.*{instPart})";
        }
        pattern += ".*$";
        return Request.Create()
            .WithPath("/api/check")
            .UsingPost()
            .WithBody(new WireMock.Matchers.RegexMatcher(pattern));
    }
}

/// <summary>Decoded view of a single captured rbac check request.</summary>
public sealed record ReceivedCheck(
    string SubjectId,
    string Permission,
    string? ResourceInstanceId,
    IReadOnlyList<string> Groups)
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ReceivedCheck From(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return new ReceivedCheck(string.Empty, string.Empty, null, Array.Empty<string>());
        }
        var raw = JsonSerializer.Deserialize<Raw>(body, ReadOpts);
        return new ReceivedCheck(
            raw?.SubjectId ?? string.Empty,
            raw?.Permission ?? string.Empty,
            raw?.ResourceInstanceId,
            raw?.Groups ?? Array.Empty<string>());
    }

    private sealed record Raw(
        string? SubjectId,
        string? Permission,
        string? ResourceInstanceId,
        IReadOnlyList<string>? Groups);
}
