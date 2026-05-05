// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// Integration tests for <c>GET /api/bindings/resolve</c> (P3.4, story
/// rivoli-ai/andy-policies#22). Exercises the endpoint over HTTP with a
/// real seed (publish + bind through the live REST surface) and asserts
/// the resolve contract: Retired filtered out, dedup prefers Mandatory,
/// missing/whitespace targetRef returns 400, unknown targets return 200
/// with empty list.
/// </summary>
public class BindingsResolveTests : IClassFixture<PoliciesApiFactory>
{
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public BindingsResolveTests(PoliciesApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static CreatePolicyRequest MinimalCreatePolicy(string name) => new(
        Name: name,
        Description: null,
        Summary: "summary",
        Enforcement: "Must",
        Severity: "Critical",
        Scopes: Array.Empty<string>(),
        RulesJson: "{}");

    private async Task<PolicyVersionDto> CreateAndPublishAsync(string slug)
    {
        var draft = await _client.PostAsJsonAsync("/api/policies", MinimalCreatePolicy(slug));
        draft.EnsureSuccessStatusCode();
        var created = (await draft.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
        var publish = await _client.PostAsJsonAsApproverAsync(
            $"/api/policies/{created.PolicyId}/versions/{created.Id}/publish",
            new LifecycleTransitionRequest("ship"));
        publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
    }

    private async Task<BindingDto> CreateBindingAsync(
        Guid policyVersionId, BindingTargetType targetType, string targetRef, BindStrength strength)
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/bindings",
            new CreateBindingRequest(policyVersionId, targetType, targetRef, strength));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<BindingDto>(JsonOptions))!;
    }

    private static string Slug(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}".Substring(0, 16);

    [Fact]
    public async Task Resolve_OnEmptyTarget_Returns200_WithEmptyList()
    {
        var resp = await _client.GetAsync(
            $"/api/bindings/resolve?targetType=Repo&targetRef={Uri.EscapeDataString($"repo:none/missing-{Guid.NewGuid():N}")}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ResolveBindingsResponse>(JsonOptions);
        body!.Count.Should().Be(0);
        body.Bindings.Should().BeEmpty();
        body.TargetType.Should().Be(BindingTargetType.Repo);
    }

    [Fact]
    public async Task Resolve_WithWhitespaceTargetRef_Returns400()
    {
        var resp = await _client.GetAsync("/api/bindings/resolve?targetType=Repo&targetRef=%20%20");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resolve_HappyPath_ReturnsActiveBindings()
    {
        var version = await CreateAndPublishAsync(Slug("res-happy"));
        var target = $"template:{Guid.NewGuid()}";
        await CreateBindingAsync(version.Id, BindingTargetType.Template, target, BindStrength.Mandatory);

        var resp = await _client.GetAsync(
            $"/api/bindings/resolve?targetType=Template&targetRef={Uri.EscapeDataString(target)}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ResolveBindingsResponse>(JsonOptions);
        body!.Count.Should().Be(1);
        var only = body.Bindings.Single();
        only.PolicyVersionId.Should().Be(version.Id);
        only.PolicyId.Should().Be(version.PolicyId);
        only.VersionNumber.Should().Be(1);
        only.VersionState.Should().Be("Active");
        only.Enforcement.Should().Be("MUST");
        only.Severity.Should().Be("critical");
        only.BindStrength.Should().Be(BindStrength.Mandatory);
    }

    [Fact]
    public async Task Resolve_FiltersOutRetiredVersion()
    {
        var version = await CreateAndPublishAsync(Slug("res-retired"));
        var target = $"template:{Guid.NewGuid()}";
        await CreateBindingAsync(version.Id, BindingTargetType.Template, target, BindStrength.Mandatory);
        // Retire the version (Active -> Retired is the emergency path).
        var retire = await _client.PostAsJsonAsync(
            $"/api/policies/{version.PolicyId}/versions/{version.Id}/retire",
            new LifecycleTransitionRequest("recall"));
        retire.EnsureSuccessStatusCode();

        var resp = await _client.GetAsync(
            $"/api/bindings/resolve?targetType=Template&targetRef={Uri.EscapeDataString(target)}");
        var body = await resp.Content.ReadFromJsonAsync<ResolveBindingsResponse>(JsonOptions);

        body!.Count.Should().Be(0);
    }

    [Fact]
    public async Task Resolve_DedupsSameVersion_PrefersMandatory()
    {
        var version = await CreateAndPublishAsync(Slug("res-dedup"));
        var target = $"tenant:{Guid.NewGuid()}";
        await CreateBindingAsync(version.Id, BindingTargetType.Tenant, target, BindStrength.Recommended);
        await CreateBindingAsync(version.Id, BindingTargetType.Tenant, target, BindStrength.Mandatory);

        var resp = await _client.GetAsync(
            $"/api/bindings/resolve?targetType=Tenant&targetRef={Uri.EscapeDataString(target)}");
        var body = await resp.Content.ReadFromJsonAsync<ResolveBindingsResponse>(JsonOptions);

        body!.Bindings.Should().ContainSingle()
            .Which.BindStrength.Should().Be(BindStrength.Mandatory);
    }

    [Fact]
    public async Task Resolve_AfterDelete_DropsBindingFromResponse()
    {
        var version = await CreateAndPublishAsync(Slug("res-del"));
        var target = $"org:{Guid.NewGuid()}";
        var binding = await CreateBindingAsync(version.Id, BindingTargetType.Org, target, BindStrength.Mandatory);

        var preDelete = await _client.GetFromJsonAsync<ResolveBindingsResponse>(
            $"/api/bindings/resolve?targetType=Org&targetRef={Uri.EscapeDataString(target)}", JsonOptions);
        preDelete!.Count.Should().Be(1);

        var del = await _client.DeleteAsync($"/api/bindings/{binding.Id}");
        del.EnsureSuccessStatusCode();

        var postDelete = await _client.GetFromJsonAsync<ResolveBindingsResponse>(
            $"/api/bindings/resolve?targetType=Org&targetRef={Uri.EscapeDataString(target)}", JsonOptions);
        postDelete!.Count.Should().Be(0);
    }
}
