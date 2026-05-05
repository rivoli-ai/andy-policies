// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
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
/// Integration tests for tighten-only enforcement at the REST surface
/// (P4.4, story rivoli-ai/andy-policies#32). Drives the full pipeline:
/// scope hierarchy seeded via the live <see cref="IScopeService"/>, an
/// ancestor Mandatory binding inserted via the DbContext, then a
/// <c>POST /api/bindings</c> for a Recommended downstream binding —
/// asserts 409 with the structured ProblemDetails payload (errorCode,
/// offendingAncestorBindingId, offendingScopeNodeId).
/// </summary>
public class BindingsTightenOnlyTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;
    private readonly HttpClient _client;

    public BindingsTightenOnlyTests(PoliciesApiFactory factory)
    {
        _factory = factory;
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
        var version = (await draft.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
        var publish = await _client.PostAsJsonAsApproverAsync(
            $"/api/policies/{version.PolicyId}/versions/{version.Id}/publish",
            new LifecycleTransitionRequest("ship"));
        publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<PolicyVersionDto>())!;
    }

    private async Task<(Guid org, Guid repo)> SeedChainAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var scopes = scope.ServiceProvider.GetRequiredService<IScopeService>();
        var unique = Guid.NewGuid().ToString("N").Substring(0, 8);
        var org = await scopes.CreateAsync(new CreateScopeNodeRequest(
            null, ScopeType.Org, $"org:tov-{unique}", $"Org {unique}"));
        var tenant = await scopes.CreateAsync(new CreateScopeNodeRequest(
            org.Id, ScopeType.Tenant, $"tenant:tov-{unique}", $"Tenant {unique}"));
        var team = await scopes.CreateAsync(new CreateScopeNodeRequest(
            tenant.Id, ScopeType.Team, $"team:tov-{unique}", $"Team {unique}"));
        var repo = await scopes.CreateAsync(new CreateScopeNodeRequest(
            team.Id, ScopeType.Repo, $"repo:tov/{unique}", $"Repo {unique}"));
        return (org.Id, repo.Id);
    }

    private async Task<Guid> SeedAncestorMandatoryAsync(Guid scopeNodeId, Guid policyVersionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bindingId = Guid.NewGuid();
        db.Bindings.Add(new Binding
        {
            Id = bindingId,
            PolicyVersionId = policyVersionId,
            TargetType = BindingTargetType.ScopeNode,
            TargetRef = $"scope:{scopeNodeId}",
            BindStrength = BindStrength.Mandatory,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = "test",
        });
        await db.SaveChangesAsync();
        return bindingId;
    }

    [Fact]
    public async Task PostBinding_WithRecommendedShadowingAncestorMandatory_Returns409_WithStructuredPayload()
    {
        var version = await CreateAndPublishAsync($"tov-{Guid.NewGuid():N}".Substring(0, 16));
        var (org, repo) = await SeedChainAsync();
        var ancestorBindingId = await SeedAncestorMandatoryAsync(org, version.Id);

        var resp = await _client.PostAsJsonAsync("/api/bindings", new
        {
            policyVersionId = version.Id,
            targetType = "ScopeNode",
            targetRef = $"scope:{repo}",
            bindStrength = "Recommended",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().Be("/problems/binding-tighten-only-violation");
        root.GetProperty("errorCode").GetString().Should().Be("binding.tighten-only-violation");
        root.GetProperty("offendingAncestorBindingId").GetGuid().Should().Be(ancestorBindingId);
        root.GetProperty("offendingScopeNodeId").GetGuid().Should().Be(org);
    }

    [Fact]
    public async Task PostBinding_UpgradingRecommendedAncestorToMandatory_Returns201()
    {
        var version = await CreateAndPublishAsync($"upg-{Guid.NewGuid():N}".Substring(0, 16));
        var (org, repo) = await SeedChainAsync();
        // Seed an ancestor Recommended; proposed Mandatory at Repo is an upgrade.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Bindings.Add(new Binding
            {
                Id = Guid.NewGuid(),
                PolicyVersionId = version.Id,
                TargetType = BindingTargetType.ScopeNode,
                TargetRef = $"scope:{org}",
                BindStrength = BindStrength.Recommended,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBySubjectId = "test",
            });
            await db.SaveChangesAsync();
        }

        var resp = await _client.PostAsJsonAsync("/api/bindings", new
        {
            policyVersionId = version.Id,
            targetType = "ScopeNode",
            targetRef = $"scope:{repo}",
            bindStrength = "Mandatory",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task PostBinding_OnSoftRef_NotResolvableToScopeNode_Allowed()
    {
        // Soft refs (targets that don't resolve to any ScopeNode) skip
        // the tighten-only walk entirely — P3 non-goal preservation.
        var version = await CreateAndPublishAsync($"soft-{Guid.NewGuid():N}".Substring(0, 16));

        var resp = await _client.PostAsJsonAsync("/api/bindings", new
        {
            policyVersionId = version.Id,
            targetType = "Repo",
            targetRef = $"repo:never/heard-of-this-{Guid.NewGuid():N}".Substring(0, 40),
            bindStrength = "Recommended",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task DeleteBinding_NeverViolatesTightenOnly()
    {
        var version = await CreateAndPublishAsync($"del-{Guid.NewGuid():N}".Substring(0, 16));
        var (org, repo) = await SeedChainAsync();

        // Create a Mandatory at Org via the REST surface (allowed —
        // proposing Mandatory under no ancestor binding for this policy).
        var orgBinding = await _client.PostAsJsonAsync("/api/bindings", new
        {
            policyVersionId = version.Id,
            targetType = "ScopeNode",
            targetRef = $"scope:{org}",
            bindStrength = "Mandatory",
        });
        orgBinding.EnsureSuccessStatusCode();
        // The validator's null-returning ValidateDeleteAsync hook means
        // the soft-delete succeeds without a tighten-only check.
        var bindingDto = (await orgBinding.Content.ReadFromJsonAsync<BindingDto>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }))!;

        var del = await _client.DeleteAsync($"/api/bindings/{bindingDto.Id}");

        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
