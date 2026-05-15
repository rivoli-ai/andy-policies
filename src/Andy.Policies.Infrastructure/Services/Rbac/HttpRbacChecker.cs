// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.Services.Rbac;

/// <summary>
/// Production <see cref="IRbacChecker"/>: calls
/// <c>POST {andy-rbac}/api/check</c> with a 60-second in-memory cache
/// keyed on <c>(subject, permission, instance)</c> and a fail-closed
/// default on transport, timeout, or non-2xx responses. P7.2,
/// story rivoli-ai/andy-policies#51.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why fail-closed?</b> A governance catalog that opens up under
/// adversity (rbac unreachable → grant access) would be a critical-path
/// failure mode. Correctness over availability for this service.
/// Network errors, request timeouts, and non-2xx responses all collapse
/// to <c>(Allowed=false, Reason="rbac-unreachable: …")</c>.
/// </para>
/// <para>
/// <b>Why cache successful decisions for 60s?</b> A cold-cache REST
/// request may hit this 3–4 times (controller [Authorize], service-layer
/// guard, bundle resolution). 60s bounds the revocation propagation
/// window without flooding andy-rbac. Both allow and deny decisions
/// cache; fail-closed decisions do <b>not</b> — so a recovered andy-rbac
/// is picked up on the next call instead of waiting out the TTL.
/// </para>
/// <para>
/// <b>Why omit groups from the cache key?</b> Groups rarely change
/// within a token's lifetime; including them would bloat cache
/// cardinality without improving correctness. andy-rbac re-resolves
/// groups server-side per request if it chooses.
/// </para>
/// </remarks>
public sealed class HttpRbacChecker : IRbacChecker
{
    public const string MeterName = "Andy.Policies.RbacChecker";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private const string FailClosedReason = "rbac-unreachable: fail-closed default";

    // andy-rbac CheckController accepts PascalCase property names; pin
    // them explicitly so the wire format does not drift if a future
    // host-default change flips PostAsJsonAsync's serializer to
    // camelCase. PropertyNameCaseInsensitive on read covers reasonable
    // server-side variation in andy-rbac responses.
    private static readonly JsonSerializerOptions WireFormat = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HttpRbacChecker> _log;
    private readonly Counter<long> _checkCounter;

    public HttpRbacChecker(
        HttpClient http,
        IMemoryCache cache,
        ILogger<HttpRbacChecker> log)
    {
        _http = http;
        _cache = cache;
        _log = log;

        var meter = new Meter(MeterName);
        _checkCounter = meter.CreateCounter<long>(
            "policies.rbac.check_total",
            description: "RBAC check outcomes by result label.");
    }

    public async Task<RbacDecision> CheckAsync(
        string subjectId,
        string permissionCode,
        IReadOnlyList<string> groups,
        string? resourceInstanceId,
        CancellationToken ct)
    {
        var key = CacheKey(subjectId, permissionCode, resourceInstanceId);
        if (_cache.TryGetValue(key, out RbacDecision? hit) && hit is not null)
        {
            _checkCounter.Add(1, new KeyValuePair<string, object?>(
                "result", hit.Allowed ? "allow" : "deny"));
            return hit;
        }

        try
        {
            var req = new RbacCheckRequest(subjectId, permissionCode, groups, resourceInstanceId);
            // Relative — no leading slash — so it appends to BaseAddress.
            // BaseAddress under Conductor is `http://localhost:9100/rbac/`
            // (the unified proxy). A leading slash would resolve against
            // the authority root and strip the /rbac/ prefix, causing the
            // request to land on the wrong route and the fail-closed
            // branch to fire on every check.
            using var resp = await _http.PostAsJsonAsync("api/check", req, WireFormat, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "rbac check non-success {Status} for {Subject} {Permission}",
                    resp.StatusCode, subjectId, permissionCode);
                return FailClosed();
            }

            var body = await resp.Content
                .ReadFromJsonAsync<RbacCheckResponse>(WireFormat, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("empty rbac response body");
            var decision = new RbacDecision(body.Allowed, body.Reason ?? string.Empty);
            _cache.Set(key, decision, CacheTtl);
            _checkCounter.Add(1, new KeyValuePair<string, object?>(
                "result", decision.Allowed ? "allow" : "deny"));
            return decision;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(
                "rbac check timed out for {Subject} {Permission}",
                subjectId, permissionCode);
            return FailClosed();
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex,
                "rbac check transport error for {Subject} {Permission}",
                subjectId, permissionCode);
            return FailClosed();
        }
    }

    private RbacDecision FailClosed()
    {
        _checkCounter.Add(1, new KeyValuePair<string, object?>("result", "fail_closed"));
        return new RbacDecision(false, FailClosedReason);
    }

    private static string CacheKey(string subjectId, string permissionCode, string? resourceInstanceId)
        => $"rbac::{subjectId}::{permissionCode}::{resourceInstanceId ?? "-"}";
}
