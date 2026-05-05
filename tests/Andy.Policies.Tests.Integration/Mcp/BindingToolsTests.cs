// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using System.Text.Json;
using Andy.Policies.Api.Mcp;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Mcp;

/// <summary>
/// Tests for <see cref="BindingTools"/> (P3.5, story
/// rivoli-ai/andy-policies#23). Drives the static tool methods directly
/// against a real <see cref="BindingService"/> + <see cref="BindingResolver"/>
/// backed by EF Core InMemory plus a real <see cref="PolicyService"/> for
/// seeding. Verifies the wire contract returned to MCP agents:
/// formatted strings on success, prefixed error codes
/// (<c>policy.binding.{not_found,retired_target,invalid_target}</c>) on
/// failure, JSON envelope on resolve, and the actor-fallback firewall.
/// </summary>
public class BindingToolsTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static (BindingService binding, BindingResolver resolver, PolicyService policies, AppDbContext db)
        NewServices()
    {
        var db = NewDb();
        return (
            new BindingService(db, new NoopAuditWriter(NullLogger<NoopAuditWriter>.Instance), TimeProvider.System),
            new BindingResolver(db),
            new PolicyService(db),
            db);
    }

    private static IHttpContextAccessor AccessorFor(string? subjectId)
    {
        var ctx = new DefaultHttpContext();
        if (subjectId is not null)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, subjectId),
            }, authenticationType: "Test"));
        }
        return new HttpContextAccessor { HttpContext = ctx };
    }

    /// <summary>
    /// P7.6 (#64) added <see cref="IRbacChecker"/> to every mutating MCP
    /// tool. Tests below thread this through; authorization-specific
    /// behaviour lives in <c>OverrideToolsRbacTests</c>.
    /// </summary>
    private static readonly IRbacChecker AllowRbac = McpToolStubs.AllowAllRbac;

    private static CreatePolicyRequest MinimalCreate(string name) => new(
        Name: name,
        Description: null,
        Summary: "summary",
        Enforcement: "Must",
        Severity: "Critical",
        Scopes: Array.Empty<string>(),
        RulesJson: "{}");

    [Fact]
    public async Task Create_OnDraftVersion_ReturnsFormattedDetail_AndPersists()
    {
        var (svc, _, policies, db) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("bind-create"), "sam");

        var output = await BindingTools.Create(
            svc, AccessorFor("agent-1"), AllowRbac,
            draft.Id.ToString(), "Repo", "repo:rivoli-ai/policy-x", "Mandatory");

        output.Should().Contain("Binding ");
        output.Should().Contain("Repo=repo:rivoli-ai/policy-x");
        output.Should().Contain("Mandatory");
        var rows = await db.Bindings.AsNoTracking().Where(b => b.PolicyVersionId == draft.Id).ToListAsync();
        rows.Should().ContainSingle().Which.CreatedBySubjectId.Should().Be("agent-1");
    }

    [Fact]
    public async Task Create_OnRetiredVersion_ReturnsRetiredTargetCode()
    {
        var (svc, _, policies, db) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("bind-retired"), "sam");
        // Force the version to Retired to exercise the service guard.
        var entity = await db.PolicyVersions.FirstAsync(v => v.Id == draft.Id);
        entity.State = LifecycleState.Retired;
        await db.SaveChangesAsync();

        var output = await BindingTools.Create(
            svc, AccessorFor("agent-1"), AllowRbac,
            draft.Id.ToString(), "Repo", "repo:any/repo", "Recommended");

        output.Should().StartWith("policy.binding.retired_target:");
    }

    [Fact]
    public async Task Create_OnUnknownVersion_ReturnsNotFoundCode()
    {
        var (svc, _, _, _) = NewServices();

        var output = await BindingTools.Create(
            svc, AccessorFor("agent-1"), AllowRbac,
            Guid.NewGuid().ToString(), "Repo", "repo:rivoli-ai/policy-x", "Mandatory");

        output.Should().StartWith("policy.binding.not_found:");
    }

    [Fact]
    public async Task Create_WithInvalidTargetType_ReturnsInvalidTargetCode()
    {
        var (svc, _, policies, _) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("bind-invalid"), "sam");

        var output = await BindingTools.Create(
            svc, AccessorFor("agent-1"), AllowRbac,
            draft.Id.ToString(), "Unicorn", "ref", "Recommended");

        output.Should().StartWith("policy.binding.invalid_target:");
    }

    [Fact]
    public async Task Create_WithEmptyTargetRef_ReturnsInvalidTargetCode()
    {
        var (svc, _, policies, _) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("bind-empty"), "sam");

        var output = await BindingTools.Create(
            svc, AccessorFor("agent-1"), AllowRbac,
            draft.Id.ToString(), "Repo", "  ", "Recommended");

        output.Should().StartWith("policy.binding.invalid_target:");
    }

    [Fact]
    public async Task Create_WithNoSubjectId_ReturnsAuthRequired_DoesNotMutate()
    {
        var (svc, _, policies, db) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("bind-noauth"), "sam");

        var output = await BindingTools.Create(
            svc, AccessorFor(subjectId: null), AllowRbac,
            draft.Id.ToString(), "Repo", "repo:any/repo", "Mandatory");

        output.Should().Contain("Authentication required");
        var rows = await db.Bindings.AsNoTracking().Where(b => b.PolicyVersionId == draft.Id).ToListAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task List_FormatsHeaderAndOneLinePerBinding()
    {
        var (svc, _, policies, _) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("bind-list"), "sam");
        await BindingTools.Create(svc, AccessorFor("agent-1"), AllowRbac,
            draft.Id.ToString(), "Repo", "repo:a/x", "Mandatory");

        var output = await BindingTools.List(svc, draft.Id.ToString());

        output.Should().Contain("1 binding on version");
        output.Should().Contain("Repo=repo:a/x");
        output.Should().Contain("(Mandatory)");
    }

    [Fact]
    public async Task List_OnInvalidGuid_ReturnsErrorString()
    {
        var (svc, _, _, _) = NewServices();

        var output = await BindingTools.List(svc, "not-a-guid");

        output.Should().StartWith("Invalid policy version id:");
    }

    [Fact]
    public async Task Delete_RoundTrips_AndSecondDeleteReturnsNotFound()
    {
        var (svc, _, policies, _) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("bind-del"), "sam");
        var createOutput = await BindingTools.Create(svc, AccessorFor("agent-1"), AllowRbac,
            draft.Id.ToString(), "Repo", "repo:a/del", "Mandatory");
        var bindingId = createOutput.Split('\n')[0].Replace("Binding ", "").Trim();

        var first = await BindingTools.Delete(svc, AccessorFor("agent-1"), AllowRbac, bindingId, "no longer needed");
        first.Should().Contain("soft-deleted");

        var second = await BindingTools.Delete(svc, AccessorFor("agent-1"), AllowRbac, bindingId);
        second.Should().StartWith("policy.binding.not_found:");
    }

    [Fact]
    public async Task Delete_WithNoSubjectId_ReturnsAuthRequired()
    {
        var (svc, _, _, _) = NewServices();

        var output = await BindingTools.Delete(svc, AccessorFor(subjectId: null), AllowRbac, Guid.NewGuid().ToString());

        output.Should().Contain("Authentication required");
    }

    [Fact]
    public async Task Resolve_ReturnsValidJsonEnvelope_MatchingDtoShape()
    {
        var (svc, resolver, policies, db) = NewServices();
        var draft = await policies.CreateDraftAsync(MinimalCreate("bind-res"), "sam");
        var entity = await db.PolicyVersions.FirstAsync(v => v.Id == draft.Id);
        entity.State = LifecycleState.Active;
        await db.SaveChangesAsync();
        await BindingTools.Create(svc, AccessorFor("agent-1"), AllowRbac,
            draft.Id.ToString(), "Template", "template:abc", "Mandatory");

        var output = await BindingTools.Resolve(resolver, "Template", "template:abc");

        // Output is JSON; parse it back to confirm the envelope shape.
        using var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("targetRef").GetString().Should().Be("template:abc");
        var first = doc.RootElement.GetProperty("bindings").EnumerateArray().First();
        first.GetProperty("policyVersionId").GetString().Should().Be(draft.Id.ToString());
        first.GetProperty("versionState").GetString().Should().Be("Active");
        first.GetProperty("enforcement").GetString().Should().Be("MUST");
        first.GetProperty("severity").GetString().Should().Be("critical");
        first.GetProperty("bindStrength").GetString().Should().Be("Mandatory");
    }

    [Fact]
    public async Task Resolve_OnInvalidTargetType_ReturnsInvalidTargetCode()
    {
        var (_, resolver, _, _) = NewServices();

        var output = await BindingTools.Resolve(resolver, "Unicorn", "anything");

        output.Should().StartWith("policy.binding.invalid_target:");
    }

    [Fact]
    public async Task Resolve_OnEmptyTargetRef_ReturnsInvalidTargetCode()
    {
        var (_, resolver, _, _) = NewServices();

        var output = await BindingTools.Resolve(resolver, "Template", "  ");

        output.Should().StartWith("policy.binding.invalid_target:");
    }
}
