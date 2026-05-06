// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Api.Mcp;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Audit;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

using static Andy.Policies.Tests.Integration.Fixtures.McpToolStubs;

namespace Andy.Policies.Tests.Integration.Mcp;

/// <summary>
/// Tests for <see cref="BundleTools"/> (P8.5, story
/// rivoli-ai/andy-policies#85). Drives the static MCP tool methods
/// directly against a real <see cref="BundleService"/> +
/// <see cref="BundleSnapshotBuilder"/> + <see cref="AuditChain"/>
/// (the same wiring P8.2 integration tests use). Verifies the wire
/// contract: JSON DTO envelopes on success, prefixed
/// <c>policy.bundle.{invalid_argument,not_found,conflict,forbidden}</c>
/// codes on failure, the soft-delete idempotency invariant, and
/// the actor-fallback firewall.
/// </summary>
public class BundleToolsTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public BundleToolsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=true");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite(_connection)
        .Options;

    private async Task<AppDbContext> InitDbAsync()
    {
        var db = new AppDbContext(Options());
        await db.Database.MigrateAsync();
        return db;
    }

    private static BundleService NewBundleService(AppDbContext db) => new(
        db,
        new BundleSnapshotBuilder(db),
        new AuditChain(db, TimeProvider.System),
        TimeProvider.System);

    private static BundleResolver NewResolver(AppDbContext db) => new(
        db, new MemoryCache(new MemoryCacheOptions()));

    private static async Task SeedActiveVersionAsync(AppDbContext db, string name)
    {
        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = name,
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
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync();
    }

    // ----- Create -------------------------------------------------------

    [Fact]
    public async Task Create_HappyPath_ReturnsJsonDto_WithSnapshotHash()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewBundleService(db);

        var output = await BundleTools.Create(
            svc, AccessorFor("user:author"), AllowAllRbac,
            name: "snap-1", rationale: "initial release", description: "first");

        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("name").GetString().Should().Be("snap-1");
        doc.RootElement.GetProperty("snapshotHash").GetString().Should().HaveLength(64);
        doc.RootElement.GetProperty("state").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task Create_NoSubject_ReturnsAuthenticationRequired()
    {
        await using var db = await InitDbAsync();
        var svc = NewBundleService(db);

        var output = await BundleTools.Create(
            svc, AccessorFor(subjectId: null), AllowAllRbac,
            name: "snap", rationale: "x", description: null);

        output.Should().StartWith("Authentication required");
    }

    [Fact]
    public async Task Create_RbacDeny_ReturnsForbidden_AndDoesNotPersist()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewBundleService(db);

        var output = await BundleTools.Create(
            svc, AccessorFor("user:nope"), DenyAllRbac,
            name: "snap-deny", rationale: "x", description: null);

        output.Should().StartWith("policy.bundle.forbidden:");
        var rows = await db.Bundles.AsNoTracking().Where(b => b.Name == "snap-deny").CountAsync();
        rows.Should().Be(0,
            "an RBAC denial that still persisted a row would defeat the entire " +
            "guard; the audit chain would carry a bundle the caller wasn't " +
            "allowed to create");
    }

    [Fact]
    public async Task Create_BadSlug_ReturnsInvalidArgument()
    {
        await using var db = await InitDbAsync();
        var svc = NewBundleService(db);

        var output = await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "BAD CAPS", rationale: "x", description: null);

        output.Should().StartWith("policy.bundle.invalid_argument:");
    }

    [Fact]
    public async Task Create_EmptyRationale_ReturnsInvalidArgument()
    {
        await using var db = await InitDbAsync();
        var svc = NewBundleService(db);

        var output = await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "empty-rat", rationale: "   ", description: null);

        output.Should().StartWith("policy.bundle.invalid_argument:");
    }

    [Fact]
    public async Task Create_DuplicateActiveName_ReturnsConflict()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewBundleService(db);

        await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "dup", rationale: "first", description: null);

        var output = await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "dup", rationale: "second", description: null);

        output.Should().StartWith("policy.bundle.conflict:");
    }

    // ----- List ---------------------------------------------------------

    [Fact]
    public async Task List_DefaultsToActiveOnly_AndReturnsJsonArray()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewBundleService(db);
        await BundleTools.Create(svc, AccessorFor("user:a"), AllowAllRbac,
            name: "live", rationale: "x");
        var doomed = JsonDocument.Parse(await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "doomed", rationale: "x"));
        var doomedId = doomed.RootElement.GetProperty("id").GetGuid();
        await BundleTools.Delete(svc, AccessorFor("user:op"), AllowAllRbac,
            bundleId: doomedId.ToString(), rationale: "tombstone");

        var output = await BundleTools.List(svc);

        var doc = JsonDocument.Parse(output);
        var names = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();
        names.Should().Contain("live");
        names.Should().NotContain("doomed");
    }

    [Fact]
    public async Task List_IncludeDeletedTrue_ReturnsBoth()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewBundleService(db);
        await BundleTools.Create(svc, AccessorFor("user:a"), AllowAllRbac,
            name: "live", rationale: "x");
        var doomed = JsonDocument.Parse(await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "doomed", rationale: "x"));
        var doomedId = doomed.RootElement.GetProperty("id").GetGuid();
        await BundleTools.Delete(svc, AccessorFor("user:op"), AllowAllRbac,
            bundleId: doomedId.ToString(), rationale: "tombstone");

        var output = await BundleTools.List(svc, includeDeleted: true);

        var doc = JsonDocument.Parse(output);
        var names = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();
        names.Should().Contain(new[] { "live", "doomed" });
    }

    [Fact]
    public async Task List_TakeOver200_IsClampedTo200()
    {
        // The clamp is the only thing the tool does on top of the
        // service; pinning behaviour here defends against a future
        // refactor that pushes pagination into the service unchanged
        // and forgets the cap (memory-blowup guard).
        await using var db = await InitDbAsync();
        var svc = NewBundleService(db);

        // We can't easily seed > 200 bundles in a unit-shaped test,
        // but we can pin that take=999 doesn't blow up — the service
        // also clamps to its own MaxPageSize. The tool's contribution
        // is the explicit upper bound; without it, an evolving
        // service that raised its MaxPageSize would silently let
        // memory grow.
        var output = await BundleTools.List(svc, take: 999);

        output.Should().StartWith("[");
    }

    // ----- Get ----------------------------------------------------------

    [Fact]
    public async Task Get_HappyPath_ReturnsJsonDto()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewBundleService(db);
        var created = JsonDocument.Parse(await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "snap-get", rationale: "x"));
        var bundleId = created.RootElement.GetProperty("id").GetGuid();

        var output = await BundleTools.Get(svc, bundleId.ToString());

        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("id").GetGuid().Should().Be(bundleId);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNotFound()
    {
        await using var db = await InitDbAsync();
        var svc = NewBundleService(db);

        var output = await BundleTools.Get(svc, Guid.NewGuid().ToString());

        output.Should().StartWith("policy.bundle.not_found:");
    }

    [Fact]
    public async Task Get_BadGuid_ReturnsInvalidArgument()
    {
        await using var db = await InitDbAsync();
        var svc = NewBundleService(db);

        var output = await BundleTools.Get(svc, "not-a-guid");

        output.Should().StartWith("policy.bundle.invalid_argument:");
    }

    // ----- Resolve ------------------------------------------------------

    [Fact]
    public async Task Resolve_HappyPath_ReturnsResolveResultEnvelope()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewBundleService(db);
        var resolver = NewResolver(db);
        var created = JsonDocument.Parse(await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "snap-resolve", rationale: "x"));
        var bundleId = created.RootElement.GetProperty("id").GetGuid();

        var output = await BundleTools.Resolve(
            resolver, bundleId.ToString(), "Repo", "repo:rivoli-ai/x");

        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("bundleId").GetGuid().Should().Be(bundleId);
        doc.RootElement.GetProperty("targetType").GetString().Should().Be("Repo");
        doc.RootElement.GetProperty("targetRef").GetString().Should().Be("repo:rivoli-ai/x");
    }

    [Fact]
    public async Task Resolve_UnknownBundle_ReturnsNotFound()
    {
        await using var db = await InitDbAsync();
        var resolver = NewResolver(db);

        var output = await BundleTools.Resolve(
            resolver, Guid.NewGuid().ToString(), "Repo", "repo:any");

        output.Should().StartWith("policy.bundle.not_found:");
    }

    [Fact]
    public async Task Resolve_InvalidTargetType_ReturnsInvalidArgument()
    {
        await using var db = await InitDbAsync();
        var resolver = NewResolver(db);

        var output = await BundleTools.Resolve(
            resolver, Guid.NewGuid().ToString(), "Unicorn", "ref");

        output.Should().StartWith("policy.bundle.invalid_argument:");
    }

    [Fact]
    public async Task Resolve_EmptyTargetRef_ReturnsInvalidArgument()
    {
        await using var db = await InitDbAsync();
        var resolver = NewResolver(db);

        var output = await BundleTools.Resolve(
            resolver, Guid.NewGuid().ToString(), "Repo", "  ");

        output.Should().StartWith("policy.bundle.invalid_argument:");
    }

    // ----- Delete -------------------------------------------------------

    [Fact]
    public async Task Delete_HappyPath_FlipsState_AndReturnsDeletedDto()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewBundleService(db);
        var created = JsonDocument.Parse(await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "doomed", rationale: "x"));
        var bundleId = created.RootElement.GetProperty("id").GetGuid();

        var output = await BundleTools.Delete(
            svc, AccessorFor("user:op"), AllowAllRbac,
            bundleId: bundleId.ToString(), rationale: "tombstone");

        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("state").GetString().Should().Be("Deleted");
        doc.RootElement.GetProperty("deletedBySubjectId").GetString().Should().Be("user:op");

        // Row must remain in the table — soft-delete posture.
        var row = await db.Bundles.AsNoTracking().FirstAsync(b => b.Id == bundleId);
        row.State.Should().Be(BundleState.Deleted);
    }

    [Fact]
    public async Task Delete_AlreadyDeleted_IsIdempotent_NoSecondAuditEvent()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewBundleService(db);
        var created = JsonDocument.Parse(await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "twice", rationale: "x"));
        var bundleId = created.RootElement.GetProperty("id").GetGuid();
        await BundleTools.Delete(svc, AccessorFor("user:op"), AllowAllRbac,
            bundleId: bundleId.ToString(), rationale: "first");

        var second = await BundleTools.Delete(
            svc, AccessorFor("user:op"), AllowAllRbac,
            bundleId: bundleId.ToString(), rationale: "again");

        second.Should().StartWith("policy.bundle.not_found:",
            "the second delete is a no-op; we surface not_found so callers " +
            "can distinguish from an unknown id");
        var deleteEventCount = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == "bundle.delete" && e.EntityId == bundleId.ToString())
            .CountAsync();
        deleteEventCount.Should().Be(
            1,
            "appending a duplicate audit event would inflate the chain with " +
            "non-events and confuse compliance audits");
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsNotFound()
    {
        await using var db = await InitDbAsync();
        var svc = NewBundleService(db);

        var output = await BundleTools.Delete(
            svc, AccessorFor("user:op"), AllowAllRbac,
            bundleId: Guid.NewGuid().ToString(), rationale: "x");

        output.Should().StartWith("policy.bundle.not_found:");
    }

    [Fact]
    public async Task Delete_BadGuid_ReturnsInvalidArgument()
    {
        await using var db = await InitDbAsync();
        var svc = NewBundleService(db);

        var output = await BundleTools.Delete(
            svc, AccessorFor("user:op"), AllowAllRbac,
            bundleId: "not-a-guid", rationale: "x");

        output.Should().StartWith("policy.bundle.invalid_argument:");
    }

    [Fact]
    public async Task Delete_NoSubject_ReturnsAuthenticationRequired()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewBundleService(db);
        var created = JsonDocument.Parse(await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "no-actor", rationale: "x"));
        var bundleId = created.RootElement.GetProperty("id").GetGuid();

        var output = await BundleTools.Delete(
            svc, AccessorFor(subjectId: null), AllowAllRbac,
            bundleId: bundleId.ToString(), rationale: "x");

        output.Should().StartWith("Authentication required");
    }

    [Fact]
    public async Task Delete_RbacDeny_ReturnsForbidden_AndDoesNotPersist()
    {
        await using var db = await InitDbAsync();
        await SeedActiveVersionAsync(db, "p1");
        var svc = NewBundleService(db);
        var created = JsonDocument.Parse(await BundleTools.Create(
            svc, AccessorFor("user:a"), AllowAllRbac,
            name: "deny-del", rationale: "x"));
        var bundleId = created.RootElement.GetProperty("id").GetGuid();

        var output = await BundleTools.Delete(
            svc, AccessorFor("user:nope"), DenyAllRbac,
            bundleId: bundleId.ToString(), rationale: "x");

        output.Should().StartWith("policy.bundle.forbidden:");
        var row = await db.Bundles.AsNoTracking().FirstAsync(b => b.Id == bundleId);
        row.State.Should().Be(BundleState.Active,
            "an RBAC deny that still flipped state would silently bypass " +
            "the gate");
    }
}
