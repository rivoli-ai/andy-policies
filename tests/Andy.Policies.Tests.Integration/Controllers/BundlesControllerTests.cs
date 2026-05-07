// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// HTTP-level integration tests for <c>GET /api/bundles/{id}/resolve</c>
/// and <c>GET /api/bundles/{id}/policies/{policyId}</c> (P8.3, story
/// rivoli-ai/andy-policies#83). Pins the wire contract: ETag from
/// snapshot hash, 304 on If-None-Match, 404 on soft-delete /
/// unknown-id, 400 on missing targetRef. The reproducibility
/// invariant — same bundle, same answer regardless of live mutations
/// — is the load-bearing assertion.
/// </summary>
public class BundlesControllerTests : IClassFixture<PoliciesApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly PoliciesApiFactory _factory;
    private readonly HttpClient _client;

    public BundlesControllerTests(PoliciesApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(Guid bundleId, string snapshotHash, Guid policyId, Guid policyVersionId)>
        SeedActiveBundleAsync(string bundleName, string policyName, string targetRef)
    {
        // Reach into the factory's DI to use the real IBundleService +
        // BundleSnapshotBuilder + AuditChain. This guarantees the bundle's
        // SnapshotJson is shape-identical to what production callers will
        // see, instead of a hand-built fixture that could drift.
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var bundles = sp.GetRequiredService<IBundleService>();

        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = policyName,
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
        var binding = new Binding
        {
            Id = Guid.NewGuid(),
            PolicyVersionId = version.Id,
            TargetType = BindingTargetType.Repo,
            TargetRef = targetRef,
            BindStrength = BindStrength.Mandatory,
            CreatedBySubjectId = "seed",
        };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        db.Bindings.Add(binding);
        await db.SaveChangesAsync();

        var dto = await bundles.CreateAsync(
            new CreateBundleRequest(bundleName, null, "initial"),
            "seed",
            CancellationToken.None);
        return (dto.Id, dto.SnapshotHash, policy.Id, version.Id);
    }

    [Fact]
    public async Task Resolve_HappyPath_Returns200_WithETagEqualToSnapshotHash()
    {
        var (bundleId, hash, _, _) = await SeedActiveBundleAsync(
            $"snap-{Guid.NewGuid():N}".Substring(0, 16),
            $"p-{Guid.NewGuid():N}".Substring(0, 12),
            "repo:rivoli-ai/x");

        var resp = await _client.GetAsync(
            $"/api/bundles/{bundleId}/resolve?targetType=Repo&targetRef=repo:rivoli-ai/x");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.ETag.Should().NotBeNull();
        resp.Headers.ETag!.Tag.Should().Be(
            $"\"{hash}\"",
            "the strong validator must equal the snapshot hash so caches and " +
            "If-None-Match clients can dedupe identical bundle reads");
        resp.Headers.CacheControl?.Public.Should().BeTrue();
        resp.Headers.CacheControl?.MaxAge.Should().Be(TimeSpan.FromDays(365));
    }

    [Fact]
    public async Task Resolve_IfNoneMatchMatchesSnapshotHash_Returns304()
    {
        var (bundleId, hash, _, _) = await SeedActiveBundleAsync(
            $"snap-{Guid.NewGuid():N}".Substring(0, 16),
            $"p-{Guid.NewGuid():N}".Substring(0, 12),
            "repo:rivoli-ai/x");

        var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/bundles/{bundleId}/resolve?targetType=Repo&targetRef=repo:rivoli-ai/x");
        req.Headers.Add("If-None-Match", $"\"{hash}\"");

        var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(
            HttpStatusCode.NotModified,
            "an If-None-Match that exactly matches the strong ETag must " +
            "short-circuit before the body is rendered");
    }

    [Fact]
    public async Task Resolve_IfNoneMatchMismatch_Returns200_WithFreshBody()
    {
        var (bundleId, _, _, _) = await SeedActiveBundleAsync(
            $"snap-{Guid.NewGuid():N}".Substring(0, 16),
            $"p-{Guid.NewGuid():N}".Substring(0, 12),
            "repo:rivoli-ai/x");

        var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/bundles/{bundleId}/resolve?targetType=Repo&targetRef=repo:rivoli-ai/x");
        req.Headers.Add("If-None-Match", "\"some-other-hash\"");

        var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Resolve_OnUnknownBundleId_Returns404()
    {
        var resp = await _client.GetAsync(
            $"/api/bundles/{Guid.NewGuid()}/resolve?targetType=Repo&targetRef=repo:any");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resolve_OnSoftDeletedBundle_Returns404()
    {
        var (bundleId, _, _, _) = await SeedActiveBundleAsync(
            $"snap-{Guid.NewGuid():N}".Substring(0, 16),
            $"p-{Guid.NewGuid():N}".Substring(0, 12),
            "repo:rivoli-ai/x");

        // Tombstone via the service so the audit chain stays consistent.
        using (var scope = _factory.Services.CreateScope())
        {
            var bundles = scope.ServiceProvider.GetRequiredService<IBundleService>();
            (await bundles.SoftDeleteAsync(bundleId, "op", "decommission", CancellationToken.None))
                .Should().BeTrue();
        }

        var resp = await _client.GetAsync(
            $"/api/bundles/{bundleId}/resolve?targetType=Repo&targetRef=repo:rivoli-ai/x");

        resp.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "soft-deleted bundles must be invisible to the resolution surface");
    }

    [Fact]
    public async Task Resolve_WithEmptyTargetRef_Returns400()
    {
        var (bundleId, _, _, _) = await SeedActiveBundleAsync(
            $"snap-{Guid.NewGuid():N}".Substring(0, 16),
            $"p-{Guid.NewGuid():N}".Substring(0, 12),
            "repo:rivoli-ai/x");

        var resp = await _client.GetAsync(
            $"/api/bundles/{bundleId}/resolve?targetType=Repo&targetRef=");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resolve_AnswerIsStable_AcrossSubsequentCatalogMutations()
    {
        // The reproducibility headline: pin a bundle, change the live
        // catalog, re-resolve against the bundle, get the same answer.
        var bundleName = $"stable-{Guid.NewGuid():N}".Substring(0, 16);
        var policyName = $"p-{Guid.NewGuid():N}".Substring(0, 12);
        var (bundleId, hash, _, _) = await SeedActiveBundleAsync(
            bundleName, policyName, "repo:rivoli-ai/stable");

        var firstUrl =
            $"/api/bundles/{bundleId}/resolve?targetType=Repo&targetRef=repo:rivoli-ai/stable";
        var firstBody = await _client.GetStringAsync(firstUrl);

        // Land a brand-new active version + a brand-new binding on the
        // same target between the two resolves. The bundle resolve must
        // not see either.
        using (var scope = _factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            var newPolicy = new Policy
            {
                Id = Guid.NewGuid(),
                Name = $"intruder-{Guid.NewGuid():N}".Substring(0, 16),
                CreatedBySubjectId = "later",
            };
            var newVersion = new PolicyVersion
            {
                Id = Guid.NewGuid(),
                PolicyId = newPolicy.Id,
                Version = 1,
                State = LifecycleState.Active,
                Enforcement = EnforcementLevel.Must,
                Severity = Severity.Critical,
                Scopes = new List<string>(),
                Summary = "post-bundle",
                RulesJson = "{}",
                CreatedBySubjectId = "later",
                ProposerSubjectId = "later",
                PublishedAt = DateTimeOffset.UtcNow,
                PublishedBySubjectId = "later",
            };
            var newBinding = new Binding
            {
                Id = Guid.NewGuid(),
                PolicyVersionId = newVersion.Id,
                TargetType = BindingTargetType.Repo,
                TargetRef = "repo:rivoli-ai/stable",
                BindStrength = BindStrength.Mandatory,
                CreatedBySubjectId = "later",
            };
            db.Policies.Add(newPolicy);
            db.PolicyVersions.Add(newVersion);
            db.Bindings.Add(newBinding);
            await db.SaveChangesAsync();
        }

        var secondBody = await _client.GetStringAsync(firstUrl);

        secondBody.Should().Be(
            firstBody,
            "the bundle is a frozen view; a new live binding for the same target " +
            "must NOT appear in the bundle resolve, otherwise pinning is broken");

        // Defence in depth: the SnapshotHash echoed in the payload must
        // still equal the original hash.
        var doc = JsonDocument.Parse(secondBody);
        doc.RootElement.GetProperty("snapshotHash").GetString()
            .Should().Be(hash);
    }

    [Fact]
    public async Task GetPinnedPolicy_HappyPath_Returns200_WithETagAndBody()
    {
        var (bundleId, hash, policyId, policyVersionId) = await SeedActiveBundleAsync(
            $"snap-{Guid.NewGuid():N}".Substring(0, 16),
            $"p-{Guid.NewGuid():N}".Substring(0, 12),
            "repo:rivoli-ai/x");

        var resp = await _client.GetAsync($"/api/bundles/{bundleId}/policies/{policyId}");
        resp.EnsureSuccessStatusCode();

        resp.Headers.ETag!.Tag.Should().Be($"\"{hash}\"");
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        dto.GetProperty("policyId").GetGuid().Should().Be(policyId);
        dto.GetProperty("policyVersionId").GetGuid().Should().Be(policyVersionId);
        dto.GetProperty("snapshotHash").GetString().Should().Be(hash);
    }

    [Fact]
    public async Task GetPinnedPolicy_OnUnknownPolicyId_Returns404()
    {
        var (bundleId, _, _, _) = await SeedActiveBundleAsync(
            $"snap-{Guid.NewGuid():N}".Substring(0, 16),
            $"p-{Guid.NewGuid():N}".Substring(0, 12),
            "repo:rivoli-ai/x");

        var resp = await _client.GetAsync(
            $"/api/bundles/{bundleId}/policies/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPinnedPolicy_OnUnknownBundleId_Returns404()
    {
        var resp = await _client.GetAsync(
            $"/api/bundles/{Guid.NewGuid()}/policies/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- P9 follow-up #204 — bulk contents tree ----------------------

    [Fact]
    public async Task GetContents_HappyPath_ReturnsTreeWithBindingsNestedUnderPolicy()
    {
        var (bundleId, hash, policyId, policyVersionId) = await SeedActiveBundleAsync(
            $"snap-{Guid.NewGuid():N}".Substring(0, 16),
            $"p-{Guid.NewGuid():N}".Substring(0, 12),
            "repo:rivoli-ai/contents");

        var resp = await _client.GetAsync($"/api/bundles/{bundleId}/contents");
        resp.EnsureSuccessStatusCode();

        resp.Headers.ETag!.Tag.Should().Be($"\"{hash}\"",
            "the contents endpoint shares the snapshot-hash strong validator with /resolve and /policies/{id}");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        body.GetProperty("bundleId").GetGuid().Should().Be(bundleId);
        body.GetProperty("snapshotHash").GetString().Should().Be(hash);

        // The shared-factory DB carries leftover policies from sibling
        // tests, so locate this test's policy by id rather than asserting
        // count==1. The shape and casing are the load-bearing assertions.
        var policyNode = body.GetProperty("policies").EnumerateArray()
            .First(p => p.GetProperty("policyId").GetGuid() == policyId);
        policyNode.GetProperty("policyVersionId").GetGuid().Should().Be(policyVersionId);
        policyNode.GetProperty("enforcement").GetString().Should().Be("SHOULD",
            "wire casing is uppercase per ADR 0001 §6");
        policyNode.GetProperty("severity").GetString().Should().Be("moderate");

        var bindingNode = policyNode.GetProperty("bindings").EnumerateArray()
            .Single(b => b.GetProperty("targetRef").GetString() == "repo:rivoli-ai/contents");
        bindingNode.GetProperty("targetType").GetString().Should().Be("Repo");
        bindingNode.GetProperty("bindStrength").GetString().Should().Be("Mandatory");
    }

    [Fact]
    public async Task GetContents_IfNoneMatchMatchesSnapshotHash_Returns304()
    {
        var (bundleId, hash, _, _) = await SeedActiveBundleAsync(
            $"snap-{Guid.NewGuid():N}".Substring(0, 16),
            $"p-{Guid.NewGuid():N}".Substring(0, 12),
            "repo:rivoli-ai/x");

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/bundles/{bundleId}/contents");
        req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{hash}\""));
        var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task GetContents_OnUnknownBundleId_Returns404()
    {
        var resp = await _client.GetAsync($"/api/bundles/{Guid.NewGuid()}/contents");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetContents_OnSoftDeletedBundle_Returns404()
    {
        var (bundleId, _, _, _) = await SeedActiveBundleAsync(
            $"snap-{Guid.NewGuid():N}".Substring(0, 16),
            $"p-{Guid.NewGuid():N}".Substring(0, 12),
            "repo:rivoli-ai/x");

        using (var scope = _factory.Services.CreateScope())
        {
            var bundles = scope.ServiceProvider.GetRequiredService<IBundleService>();
            await bundles.SoftDeleteAsync(bundleId, "seed", "tombstone", CancellationToken.None);
        }

        var resp = await _client.GetAsync($"/api/bundles/{bundleId}/contents");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
