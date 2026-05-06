// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Xunit;

namespace Andy.Policies.Tests.E2E.EmbeddedSmoke;

/// <summary>
/// P10.4 (rivoli-ai/andy-policies#39) — cross-service embedded smoke.
/// Drives the full lifecycle through the live REST surface against a
/// running stack (default: <c>docker-compose.e2e.yml</c>; overridable
/// via env vars per <see cref="EmbeddedTestEnvironment"/>):
/// <list type="number">
///   <item><c>POST /api/policies</c> — create + first draft version.</item>
///   <item><c>POST /api/policies/{id}/versions/{vId}/publish</c> — Active.</item>
///   <item><c>POST /api/bindings</c> — bind Active version to a synthetic repo target.</item>
///   <item><c>POST /api/bundles</c> — snapshot the catalog (required for resolve under bundle pinning).</item>
///   <item><c>GET /api/bindings/resolve?bundleId=...</c> — Conductor-style read.</item>
///   <item><c>GET /api/audit</c> + chain link verification on the client.</item>
///   <item><c>GET /api/audit/verify</c> — server-side hash chain verification.</item>
/// </list>
/// Plus a negative test that proves <see cref="AuditChainVerifier"/>
/// detects a deliberately-tampered chain (link severance) — the
/// acceptance criterion's tamper assertion.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skip when E2E_ENABLED is not 1.</b> Mirrors
/// <c>EndToEndAuthSmokeTest</c> so dev-machine <c>dotnet test</c> runs
/// without Docker stay green. CI gates this behind a label or workflow
/// dispatch (deferred to a follow-up); Conductor Epic AO sets the flag
/// + <c>ANDY_POLICIES_E2E_NO_COMPOSE=1</c> so it manages compose itself.
/// </para>
/// <para>
/// <b>Why a synthetic Repo target?</b> Avoids depending on seeded scope
/// nodes or stock policies — the smoke creates everything it needs and
/// keys on a fresh GUID-suffixed repo ref so reruns against the same
/// volume don't collide.
/// </para>
/// </remarks>
public sealed class EmbeddedCrossServiceSmokeTests : IClassFixture<EmbeddedSmokeFixture>
{
    private readonly EmbeddedSmokeFixture _fx;

    public EmbeddedCrossServiceSmokeTests(EmbeddedSmokeFixture fx)
    {
        _fx = fx;
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task FullLifecycle_DraftPublishBindBundleResolveAuditVerify()
    {
        if (!_fx.IsEnabled || _fx.PoliciesClient is null) return;

        var http = _fx.PoliciesClient;
        var slug = $"e2e-smoke-{Guid.NewGuid():N}".Substring(0, 24);
        var targetRef = $"repo:rivoli-ai/smoke-{Guid.NewGuid():N}";

        // 1. Create policy + first draft version.
        var createReq = new CreatePolicyRequest(
            Name: slug,
            Description: "P10.4 cross-service smoke",
            Summary: "smoke",
            Enforcement: "Must",
            Severity: "Critical",
            Scopes: new[] { "prod" },
            RulesJson: "{}");
        var createResp = await http.PostAsJsonAsync("api/policies", createReq);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var draft = await ReadJsonAsync<PolicyVersionDto>(createResp);
        Assert.Equal("Draft", draft.State);
        Assert.Equal(1, draft.Version);

        // 2. Publish the draft.
        var publishResp = await http.PostAsJsonAsync(
            $"api/policies/{draft.PolicyId}/versions/{draft.Id}/publish",
            new LifecycleTransitionRequest(Rationale: "smoke publish"));
        Assert.Equal(HttpStatusCode.OK, publishResp.StatusCode);
        var active = await ReadJsonAsync<PolicyVersionDto>(publishResp);
        Assert.Equal("Active", active.State);
        Assert.Equal(draft.Id, active.Id);

        // 3. Bind the active version to a synthetic repo target.
        var bindReq = new CreateBindingRequest(
            PolicyVersionId: active.Id,
            TargetType: BindingTargetType.Repo,
            TargetRef: targetRef,
            BindStrength: BindStrength.Mandatory);
        var bindResp = await http.PostAsJsonAsync("api/bindings", bindReq);
        Assert.Equal(HttpStatusCode.Created, bindResp.StatusCode);
        var binding = await ReadJsonAsync<BindingDto>(bindResp);
        Assert.Equal(active.Id, binding.PolicyVersionId);

        // 4. Bundle the catalog so the pinning gate (default ON) lets
        //    resolve through.
        var bundleReq = new CreateBundleRequest(
            Name: $"smoke-bundle-{Guid.NewGuid():N}".Substring(0, 24),
            Description: "P10.4 smoke",
            Rationale: "smoke bundle");
        var bundleResp = await http.PostAsJsonAsync("api/bundles", bundleReq);
        Assert.Equal(HttpStatusCode.Created, bundleResp.StatusCode);
        var bundle = await ReadJsonAsync<BundleDto>(bundleResp);

        // 5. Resolve the binding through the Conductor-style read path.
        var resolveResp = await http.GetAsync(
            $"api/bindings/resolve?targetType={BindingTargetType.Repo}&targetRef={Uri.EscapeDataString(targetRef)}&bundleId={bundle.Id}");
        Assert.Equal(HttpStatusCode.OK, resolveResp.StatusCode);
        var resolved = await ReadJsonAsync<ResolveBindingsResponse>(resolveResp);
        Assert.Equal(targetRef, resolved.TargetRef);
        Assert.Contains(resolved.Bindings, b => b.PolicyVersionId == active.Id);

        // 6. Audit page — must include events for our slug.
        var auditResp = await http.GetAsync("api/audit?pageSize=200");
        Assert.Equal(HttpStatusCode.OK, auditResp.StatusCode);
        var page = await ReadJsonAsync<AuditPageDto>(auditResp);
        Assert.NotEmpty(page.Items);

        var ourPolicyEvents = page.Items
            .Where(e => e.EntityId == active.PolicyId.ToString()
                        || e.EntityId == active.Id.ToString())
            .ToList();
        Assert.NotEmpty(ourPolicyEvents);

        // Client-side chain link integrity over the page (server returns
        // descending by seq; we sort ascending for the verifier).
        var ascending = page.Items.OrderBy(e => e.Seq).ToList();
        var (linkValid, linkReason) = AuditChainVerifier.Verify(ascending,
            expectFromGenesis: ascending[0].Seq == 1);
        Assert.True(linkValid, linkReason);

        // 7. Server-side chain hash verification — the canonical assertion.
        var verifyResp = await http.GetAsync("api/audit/verify");
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);
        var verification = await ReadJsonAsync<ChainVerificationDto>(verifyResp);
        Assert.True(verification.Valid,
            $"server audit chain reported invalid; firstDivergenceSeq={verification.FirstDivergenceSeq}");
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task ResolveOnUnboundTarget_Returns404OrEmpty()
    {
        if (!_fx.IsEnabled || _fx.PoliciesClient is null) return;

        // The pinning gate requires a bundle even for a miss — list
        // the most recent live bundle (or create one if the catalog is
        // empty of bundles, which is unlikely here since the happy
        // path test creates one). Any 2xx body is acceptable as the
        // pin source; we only care about resolve's behaviour for an
        // unknown target.
        var http = _fx.PoliciesClient;
        var bundles = await http.GetFromJsonAsync<List<BundleDto>>("api/bundles?take=1");
        Assert.NotNull(bundles);
        if (bundles!.Count == 0)
        {
            // No prior bundle — make one so resolve has a pin target.
            var bundleResp = await http.PostAsJsonAsync("api/bundles",
                new CreateBundleRequest(
                    Name: $"smoke-empty-{Guid.NewGuid():N}".Substring(0, 24),
                    Description: null,
                    Rationale: "smoke empty"));
            bundleResp.EnsureSuccessStatusCode();
            bundles.Add((await ReadJsonAsync<BundleDto>(bundleResp))!);
        }
        var pin = bundles[0];
        var unknownRef = $"repo:rivoli-ai/no-such-{Guid.NewGuid():N}";

        var resp = await http.GetAsync(
            $"api/bindings/resolve?targetType={BindingTargetType.Repo}&targetRef={Uri.EscapeDataString(unknownRef)}&bundleId={pin.Id}");
        // The resolver returns 200 with an empty list for misses (P3.4
        // / P8.3 behaviour) — empty is the contract, not 404. We
        // accept either to keep the test resilient against future
        // tightening.
        Assert.True(resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"expected 200 or 404 for unbound target, got {(int)resp.StatusCode}");
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            var body = await ReadJsonAsync<ResolveBindingsResponse>(resp);
            Assert.Empty(body.Bindings);
        }
    }

    [Fact]
    public void AuditChainVerifier_DetectsTamperedLink()
    {
        // Pure unit-style assertion — runs whether or not the live
        // stack is up. Acceptance criterion: the verifier detects a
        // deliberately-tampered event and fails the test.
        var genesis = MakeEvent(seq: 1,
            prevHash: AuditChainVerifier.GenesisPrevHash,
            hash: "aaaa");
        var legitChild = MakeEvent(seq: 2, prevHash: "aaaa", hash: "bbbb");
        var tamperedChild = legitChild with { PrevHashHex = "deadbeef" };

        var (validHappy, _) = AuditChainVerifier.Verify(
            new[] { genesis, legitChild }, expectFromGenesis: true);
        Assert.True(validHappy);

        var (validTampered, reason) = AuditChainVerifier.Verify(
            new[] { genesis, tamperedChild }, expectFromGenesis: true);
        Assert.False(validTampered);
        Assert.NotNull(reason);
        Assert.Contains("chain link broken", reason);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(body, JsonOpts)
            ?? throw new InvalidOperationException(
                $"deserialised null from {resp.RequestMessage?.RequestUri}: {body}");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static AuditEventDto MakeEvent(long seq, string prevHash, string hash)
        => new(
            Id: Guid.NewGuid(),
            Seq: seq,
            PrevHashHex: prevHash,
            HashHex: hash,
            Timestamp: DateTimeOffset.UtcNow,
            ActorSubjectId: "smoke",
            ActorRoles: Array.Empty<string>(),
            Action: "smoke.test",
            EntityType: "Smoke",
            EntityId: Guid.Empty.ToString(),
            FieldDiff: JsonDocument.Parse("[]").RootElement,
            Rationale: null);
}
