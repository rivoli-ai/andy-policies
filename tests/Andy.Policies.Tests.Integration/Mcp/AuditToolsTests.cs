// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using Andy.Policies.Api.Mcp;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Infrastructure.Audit;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

using static Andy.Policies.Tests.Integration.Fixtures.McpToolStubs;

namespace Andy.Policies.Tests.Integration.Mcp;

/// <summary>
/// P6.7 (#48) — exercises the four MCP audit tools against a
/// real <see cref="AuditChain"/> + <see cref="AuditQuery"/> +
/// <see cref="AuditExporter"/> stack backed by SQLite. Drives
/// the static tool methods directly (the MCP transport is
/// covered by the existing
/// <c>BindingToolsTests</c> / <c>OverrideToolsTests</c>
/// patterns; here we focus on each tool's input validation +
/// happy-path output shape).
/// </summary>
public class AuditToolsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AuditChain _chain;
    private readonly AuditQuery _query;
    private readonly AuditExporter _exporter;

    public AuditToolsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.Migrate();
        _chain = new AuditChain(_db, TimeProvider.System);
        _query = new AuditQuery(_db);
        _exporter = new AuditExporter(_db, TimeProvider.System, NoRetention.Instance);
    }

    private sealed class NoRetention : IAuditRetentionPolicy
    {
        public static readonly NoRetention Instance = new();
        public DateTimeOffset? GetStalenessThreshold(DateTimeOffset now) => null;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task SeedAsync(int count, string actor = "user:test", string action = "policy.update")
    {
        for (var i = 1; i <= count; i++)
        {
            await _chain.AppendAsync(new AuditAppendRequest(
                Action: action,
                EntityType: "Policy",
                EntityId: $"policy-{i}",
                FieldDiffJson: $"[{{\"op\":\"replace\",\"path\":\"/n\",\"value\":{i}}}]",
                Rationale: $"event #{i}",
                ActorSubjectId: actor,
                ActorRoles: new[] { "admin" }), CancellationToken.None);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ----- list -------------------------------------------------------

    [Fact]
    public async Task List_HappyPath_ReturnsAuditPageJson()
    {
        await SeedAsync(3);

        var output = await AuditTools.List(_query, NoRetention.Instance, TimeProvider.System);

        var page = JsonSerializer.Deserialize<AuditPageDto>(output, JsonOpts);
        page!.Items.Should().HaveCount(3);
        page.NextCursor.Should().BeNull();
        page.PageSize.Should().Be(50);
    }

    [Fact]
    public async Task List_PageSizeOutOfRange_ReturnsInvalidArgument()
    {
        var output = await AuditTools.List(_query, NoRetention.Instance, TimeProvider.System, pageSize: 501);

        output.Should().StartWith("policy.audit.invalid_argument:");
        output.Should().Contain("pageSize");
    }

    [Fact]
    public async Task List_BadFromTimestamp_ReturnsInvalidArgument()
    {
        var output = await AuditTools.List(_query, NoRetention.Instance, TimeProvider.System, from: "not-a-date");

        output.Should().StartWith("policy.audit.invalid_argument:");
    }

    [Fact]
    public async Task List_FromGreaterThanTo_ReturnsInvalidArgument()
    {
        var output = await AuditTools.List(_query, NoRetention.Instance, TimeProvider.System,
            from: "2026-12-31T00:00:00Z",
            to: "2026-01-01T00:00:00Z");

        output.Should().StartWith("policy.audit.invalid_argument:");
    }

    [Fact]
    public async Task List_MalformedCursor_ReturnsInvalidArgument()
    {
        var output = await AuditTools.List(_query, NoRetention.Instance, TimeProvider.System, cursor: "not-base64-content!");

        output.Should().StartWith("policy.audit.invalid_argument:");
    }

    [Fact]
    public async Task List_ActorFilter_NarrowsResults()
    {
        await SeedAsync(2, actor: "user:alice");
        await SeedAsync(3, actor: "user:bob");

        var output = await AuditTools.List(_query, NoRetention.Instance, TimeProvider.System, actor: "user:alice");
        var page = JsonSerializer.Deserialize<AuditPageDto>(output, JsonOpts);

        page!.Items.Should().HaveCount(2);
        page.Items.Should().AllSatisfy(e => e.ActorSubjectId.Should().Be("user:alice"));
    }

    // ----- get --------------------------------------------------------

    [Fact]
    public async Task Get_BadGuid_ReturnsInvalidArgument()
    {
        var output = await AuditTools.Get(_query, "not-a-guid");
        output.Should().StartWith("policy.audit.invalid_argument:");
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNotFound()
    {
        var output = await AuditTools.Get(_query, Guid.NewGuid().ToString());
        output.Should().StartWith("policy.audit.not_found:");
    }

    [Fact]
    public async Task Get_ExistingId_ReturnsJsonDto()
    {
        await SeedAsync(1);
        var page = JsonSerializer.Deserialize<AuditPageDto>(
            await AuditTools.List(_query, NoRetention.Instance, TimeProvider.System), JsonOpts);
        var id = page!.Items[0].Id;

        var output = await AuditTools.Get(_query, id.ToString());

        var dto = JsonSerializer.Deserialize<AuditEventDto>(output, JsonOpts);
        dto!.Id.Should().Be(id);
    }

    // ----- verify -----------------------------------------------------

    [Fact]
    public async Task Verify_HappyPath_ReturnsValidJson()
    {
        await SeedAsync(5);
        var output = await AuditTools.Verify(_chain, AccessorFor("test-user"), AllowAllRbac);

        var dto = JsonSerializer.Deserialize<ChainVerificationDto>(output, JsonOpts);
        dto!.Valid.Should().BeTrue();
        dto.InspectedCount.Should().Be(5);
        dto.LastSeq.Should().Be(5);
    }

    [Fact]
    public async Task Verify_FromGreaterThanTo_ReturnsInvalidArgument()
    {
        var output = await AuditTools.Verify(_chain, AccessorFor("test-user"), AllowAllRbac, fromSeq: 10, toSeq: 5);
        output.Should().StartWith("policy.audit.invalid_argument:");
    }

    [Fact]
    public async Task Verify_NonPositiveBound_ReturnsInvalidArgument()
    {
        var output = await AuditTools.Verify(_chain, AccessorFor("test-user"), AllowAllRbac, fromSeq: 0);
        output.Should().StartWith("policy.audit.invalid_argument:");
    }

    // ----- export -----------------------------------------------------

    [Fact]
    public async Task Export_HappyPath_ReturnsBase64NdjsonWithSummary()
    {
        await SeedAsync(3);

        var base64 = await AuditTools.Export(_exporter, AccessorFor("test-user"), AllowAllRbac);

        var bytes = Convert.FromBase64String(base64);
        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(4, "3 events + 1 summary");

        var summary = JsonDocument.Parse(lines[^1]).RootElement;
        summary.GetProperty("type").GetString().Should().Be("summary");
        summary.GetProperty("count").GetInt64().Should().Be(3);
    }

    [Fact]
    public async Task Export_BoundedRange_HonorsBounds()
    {
        await SeedAsync(10);

        var base64 = await AuditTools.Export(_exporter, AccessorFor("test-user"), AllowAllRbac, fromSeq: 4, toSeq: 7);

        var bytes = Convert.FromBase64String(base64);
        var lines = Encoding.UTF8.GetString(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(5, "4 events (4..7) + 1 summary");
    }

    [Fact]
    public async Task Export_FromGreaterThanTo_ReturnsInvalidArgument()
    {
        var output = await AuditTools.Export(_exporter, AccessorFor("test-user"), AllowAllRbac, fromSeq: 10, toSeq: 5);
        output.Should().StartWith("policy.audit.invalid_argument:");
    }
}
