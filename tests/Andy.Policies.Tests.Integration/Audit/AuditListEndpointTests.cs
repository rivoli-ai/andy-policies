// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Andy.Policies.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Andy.Policies.Tests.Integration.Audit;

/// <summary>
/// P6.6 (#46) — exercises <c>GET /api/audit</c> end-to-end:
/// cursor pagination over 150 rows visits each event exactly
/// once with no gaps; oversized page rejected; filters
/// (actor, entityType+entityId, action) narrow correctly;
/// empty result returns an empty array, not 404; malformed
/// cursor → 400.
/// </summary>
public class AuditListEndpointTests : IDisposable
{
    /// <summary>
    /// Per-test factory (each test gets a fresh empty chain).
    /// Cursor-pagination assertions over a known event count
    /// would be flaky if rows leaked between tests via the
    /// shared <see cref="PoliciesApiFactory"/>.
    /// </summary>
    private sealed class FreshAuditFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public FreshAuditFactory()
        {
            _connection.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["AndyAuth:Authority"] = "https://test-auth.invalid",
                    ["AndySettings:ApiBaseUrl"] = "https://test-settings.invalid",
                });
            });
            builder.ConfigureServices(services =>
            {
                var ctxDescriptor = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (ctxDescriptor is not null) services.Remove(ctxDescriptor);
                services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

                services.ReplaceWithAllowAll();

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
                services.PostConfigure<AuthorizationOptions>(opts =>
                {
                    opts.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .Build();
                });

                using var sp = services.BuildServiceProvider();
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _connection.Dispose();
            base.Dispose(disposing);
        }
    }

    private readonly FreshAuditFactory _factory = new();
    private readonly HttpClient _client;

    public AuditListEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        _client.Dispose();
    }

    private async Task SeedAsync(int count, string actor = "user:test", string action = "policy.update")
    {
        using var scope = _factory.Services.CreateScope();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();
        for (var i = 1; i <= count; i++)
        {
            await chain.AppendAsync(new AuditAppendRequest(
                Action: action,
                EntityType: "Policy",
                EntityId: $"00000000-0000-0000-0000-{i:D12}",
                FieldDiffJson: $"[{{\"op\":\"replace\",\"path\":\"/n\",\"value\":{i}}}]",
                Rationale: $"event #{i}",
                ActorSubjectId: actor,
                ActorRoles: new[] { "admin" }), CancellationToken.None);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EmptyChain_ReturnsEmptyArray_Not404()
    {
        var resp = await _client.GetAsync("/api/audit");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await resp.Content.ReadFromJsonAsync<AuditPageDto>(JsonOpts);
        page!.Items.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
        page.PageSize.Should().Be(50);
    }

    [Fact]
    public async Task Walk150Events_ThroughThreePages_VisitsEachOnce()
    {
        await SeedAsync(150);

        var seen = new HashSet<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            pages++;
            var url = cursor is null
                ? "/api/audit?pageSize=50"
                : $"/api/audit?pageSize=50&cursor={Uri.EscapeDataString(cursor)}";
            var resp = await _client.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var page = await resp.Content.ReadFromJsonAsync<AuditPageDto>(JsonOpts);
            page!.Items.Should().NotBeNull();
            foreach (var item in page.Items)
            {
                seen.Add(item.Seq).Should().BeTrue("each Seq must be visited exactly once");
            }
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.Count.Should().Be(150);
        seen.Min().Should().Be(1);
        seen.Max().Should().Be(150);
        pages.Should().Be(3, "150 events / 50 per page = 3 pages");
    }

    [Fact]
    public async Task PageSizeOverMax_Returns400()
    {
        var resp = await _client.GetAsync("/api/audit?pageSize=501");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        problem.GetProperty("type").GetString().Should().Be("/problems/audit-list-page-size");
        problem.GetProperty("errorCode").GetString().Should().Be("audit.list.invalid_page_size");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PageSizeNonPositive_Returns400(int size)
    {
        var resp = await _client.GetAsync($"/api/audit?pageSize={size}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MalformedCursor_Returns400_WithErrorCode()
    {
        var resp = await _client.GetAsync("/api/audit?cursor=not-base64-content!");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        problem.GetProperty("type").GetString().Should().Be("/problems/audit-list-cursor");
        problem.GetProperty("errorCode").GetString().Should().Be("audit.list.invalid_cursor");
    }

    [Fact]
    public async Task FromGreaterThanTo_Returns400()
    {
        var resp = await _client.GetAsync(
            "/api/audit?from=2026-12-31T00:00:00Z&to=2026-01-01T00:00:00Z");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        problem.GetProperty("errorCode").GetString().Should().Be("audit.list.invalid_range");
    }

    [Fact]
    public async Task ActorFilter_NarrowsToMatchingRows()
    {
        await SeedAsync(5, actor: "user:alice");
        await SeedAsync(7, actor: "user:bob");

        var resp = await _client.GetAsync("/api/audit?actor=user:alice");

        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<AuditPageDto>(JsonOpts);
        page!.Items.Should().HaveCount(5);
        page.Items.Should().AllSatisfy(e => e.ActorSubjectId.Should().Be("user:alice"));
    }

    [Fact]
    public async Task EntityTypeAndIdFilter_HitsCompositeIndex()
    {
        await SeedAsync(3);
        // Seed a different entity to make the filter meaningful.
        using var scope = _factory.Services.CreateScope();
        var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();
        await chain.AppendAsync(new AuditAppendRequest(
            Action: "binding.create",
            EntityType: "Binding",
            EntityId: "binding-42",
            FieldDiffJson: "[]",
            Rationale: "irrelevant",
            ActorSubjectId: "user:test",
            ActorRoles: new[] { "admin" }), CancellationToken.None);

        var resp = await _client.GetAsync(
            "/api/audit?entityType=Binding&entityId=binding-42");

        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<AuditPageDto>(JsonOpts);
        page!.Items.Should().ContainSingle().Which.EntityType.Should().Be("Binding");
    }

    [Fact]
    public async Task ActionFilter_NarrowsToMatchingAction()
    {
        await SeedAsync(3, action: "policy.update");
        await SeedAsync(2, action: "policy.publish");

        var resp = await _client.GetAsync("/api/audit?action=policy.publish");

        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<AuditPageDto>(JsonOpts);
        page!.Items.Should().HaveCount(2);
        page.Items.Should().AllSatisfy(e => e.Action.Should().Be("policy.publish"));
    }

    [Fact]
    public async Task ResponseDto_FieldDiff_IsParsedJsonArray_NotEncodedString()
    {
        // Acceptance criterion: fieldDiff is a JSON array, not a
        // JSON-encoded string. Easiest assertion: read the raw
        // body and check the field's JSON value type.
        await SeedAsync(1);

        var resp = await _client.GetAsync("/api/audit");
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var firstItem = doc.RootElement.GetProperty("items")[0];
        var fieldDiff = firstItem.GetProperty("fieldDiff");

        fieldDiff.ValueKind.Should().Be(JsonValueKind.Array,
            "the JSON Patch document must travel as an array");
        fieldDiff[0].GetProperty("op").GetString().Should().Be("replace");
    }

    [Fact]
    public async Task HashHex_IsLowercase64Char()
    {
        await SeedAsync(1);

        var resp = await _client.GetAsync("/api/audit");
        var page = await resp.Content.ReadFromJsonAsync<AuditPageDto>(JsonOpts);

        var item = page!.Items.Single();
        item.HashHex.Should().HaveLength(64);
        item.HashHex.Should().Be(item.HashHex.ToLowerInvariant());
        item.PrevHashHex.Should().HaveLength(64);
        item.PrevHashHex.Should().Be(item.PrevHashHex.ToLowerInvariant());
    }
}
