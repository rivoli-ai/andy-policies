// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Infrastructure.Audit;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Shared.Auditing;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Integration.Audit;

/// <summary>
/// P6.7 (#48) — exercises <see cref="AuditExporter"/> at three
/// layers:
/// <list type="bullet">
///   <item>Format — N event lines (<c>"type":"event"</c>) +
///     trailing summary line (<c>"type":"summary"</c>) with
///     correct counts and terminal hash.</item>
///   <item>Round-trip — the exported bundle re-verifies
///     against the live chain (the offline verifier
///     re-computes hashes via Shared
///     <see cref="AuditEnvelopeHasher"/>; live writer + offline
///     verifier produce byte-identical results).</item>
///   <item>Streaming memory budget — 1,000-event export keeps
///     peak heap within a pragmatic bound (≤ 32 MB delta).</item>
/// </list>
/// </summary>
public class AuditExporterTests
{
    private static (AppDbContext db, IAuditChain chain, IAuditExporter exporter, SqliteConnection conn) NewStack()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new AppDbContext(options);
        db.Database.Migrate();
        var chain = new AuditChain(db, TimeProvider.System);
        var exporter = new AuditExporter(db, TimeProvider.System, NoRetention.Instance);
        return (db, chain, exporter, conn);
    }

    private sealed class NoRetention : IAuditRetentionPolicy
    {
        public static readonly NoRetention Instance = new();
        public DateTimeOffset? GetStalenessThreshold(DateTimeOffset now) => null;
    }

    private static async Task SeedAsync(IAuditChain chain, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            await chain.AppendAsync(new AuditAppendRequest(
                Action: "policy.update",
                EntityType: "Policy",
                EntityId: $"policy-{i}",
                FieldDiffJson: $"[{{\"op\":\"replace\",\"path\":\"/n\",\"value\":{i}}}]",
                Rationale: $"event #{i}",
                ActorSubjectId: "user:test",
                ActorRoles: new[] { "admin" }), CancellationToken.None);
        }
    }

    private static List<JsonElement> ParseLines(string ndjson)
    {
        return ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();
    }

    [Fact]
    public async Task Export_ThreeEvents_EmitsThreeEventLinesPlusSummary()
    {
        var (db, chain, exporter, conn) = NewStack();
        try
        {
            await SeedAsync(chain, 3);
            using var ms = new MemoryStream();

            await exporter.WriteNdjsonAsync(ms, fromSeq: null, toSeq: null, CancellationToken.None);

            var lines = ParseLines(Encoding.UTF8.GetString(ms.ToArray()));
            lines.Should().HaveCount(4, "3 events + 1 summary");
            lines.Take(3).Should().AllSatisfy(l =>
                l.GetProperty("type").GetString().Should().Be("event"));

            var summary = lines[^1];
            summary.GetProperty("type").GetString().Should().Be("summary");
            summary.GetProperty("count").GetInt64().Should().Be(3);
            summary.GetProperty("fromSeq").GetInt64().Should().Be(1);
            summary.GetProperty("toSeq").GetInt64().Should().Be(3);
            summary.GetProperty("genesisPrevHashHex").GetString().Should().Be(new string('0', 64));

            var lastEvent = lines[2];
            summary.GetProperty("terminalHashHex").GetString()
                .Should().Be(lastEvent.GetProperty("hashHex").GetString());
        }
        finally
        {
            db.Dispose();
            conn.Dispose();
        }
    }

    [Fact]
    public async Task Export_EmptyChain_EmitsOnlySummary()
    {
        var (db, _, exporter, conn) = NewStack();
        try
        {
            using var ms = new MemoryStream();
            await exporter.WriteNdjsonAsync(ms, null, null, CancellationToken.None);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            var lines = ParseLines(text);
            lines.Should().ContainSingle("empty chain still emits a summary trailer");
            var summary = lines[0];
            summary.GetProperty("type").GetString().Should().Be("summary");
            summary.GetProperty("count").GetInt64().Should().Be(0);
            summary.GetProperty("genesisPrevHashHex").GetString().Should().Be(new string('0', 64));
            summary.GetProperty("terminalHashHex").GetString().Should().Be(new string('0', 64));
        }
        finally
        {
            db.Dispose();
            conn.Dispose();
        }
    }

    [Fact]
    public async Task Export_BoundedRange_HonorsFromAndTo()
    {
        var (db, chain, exporter, conn) = NewStack();
        try
        {
            await SeedAsync(chain, 10);
            using var ms = new MemoryStream();

            await exporter.WriteNdjsonAsync(ms, fromSeq: 4, toSeq: 7, CancellationToken.None);

            var lines = ParseLines(Encoding.UTF8.GetString(ms.ToArray()));
            lines.Should().HaveCount(5, "4 events (4..7) + 1 summary");
            var summary = lines[^1];
            summary.GetProperty("count").GetInt64().Should().Be(4);
            summary.GetProperty("fromSeq").GetInt64().Should().Be(4);
            summary.GetProperty("toSeq").GetInt64().Should().Be(7);
        }
        finally
        {
            db.Dispose();
            conn.Dispose();
        }
    }

    [Fact]
    public async Task Export_Bundle_VerifiesViaSharedHasher()
    {
        // The acceptance criterion: the exported bundle is verifiable
        // offline. Drive the verification loop in-process via the
        // Shared AuditEnvelopeHasher (the same code the CLI's
        // --file mode uses) to prove the format round-trips.
        var (db, chain, exporter, conn) = NewStack();
        try
        {
            await SeedAsync(chain, 5);
            using var ms = new MemoryStream();
            await exporter.WriteNdjsonAsync(ms, null, null, CancellationToken.None);

            var lines = ParseLines(Encoding.UTF8.GetString(ms.ToArray()));
            var prev = new byte[32];
            foreach (var line in lines)
            {
                if (line.GetProperty("type").GetString() == "summary") continue;
                var declaredPrev = Convert.FromHexString(line.GetProperty("prevHashHex").GetString()!);
                declaredPrev.AsSpan().SequenceEqual(prev).Should().BeTrue();

                var roles = line.GetProperty("actorRoles")
                    .EnumerateArray().Select(e => e.GetString()!).ToList();
                var fieldDiff = line.GetProperty("fieldDiff").GetRawText();
                var recomputed = AuditEnvelopeHasher.ComputeHash(
                    prev,
                    line.GetProperty("id").GetGuid(),
                    line.GetProperty("timestamp").GetDateTimeOffset(),
                    line.GetProperty("actorSubjectId").GetString()!,
                    roles,
                    line.GetProperty("action").GetString()!,
                    line.GetProperty("entityType").GetString()!,
                    line.GetProperty("entityId").GetString()!,
                    fieldDiff,
                    line.TryGetProperty("rationale", out var rEl) && rEl.ValueKind == JsonValueKind.String
                        ? rEl.GetString()
                        : null);
                var declaredHash = Convert.FromHexString(line.GetProperty("hashHex").GetString()!);
                recomputed.AsSpan().SequenceEqual(declaredHash).Should().BeTrue();
                prev = declaredHash;
            }
        }
        finally
        {
            db.Dispose();
            conn.Dispose();
        }
    }

    [Fact]
    public async Task Export_ThousandEvents_StreamsWithBoundedMemory()
    {
        var (db, chain, exporter, conn) = NewStack();
        try
        {
            await SeedAsync(chain, 1000);

            // Force a clean baseline before measuring.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
            var stopwatch = Stopwatch.StartNew();

            using var ms = new MemoryStream();
            await exporter.WriteNdjsonAsync(ms, null, null, CancellationToken.None);
            stopwatch.Stop();

            var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
            var delta = heapAfter - heapBefore;

            // Each event line is ~600 bytes serialised; 1000 events
            // → ~600 KB on the wire. The buffered MemoryStream
            // dominates the heap delta. Generous 32 MB ceiling
            // catches O(N²) regressions (e.g. accidental string
            // concatenation in the writer) without flaking on
            // GC noise.
            delta.Should().BeLessThan(32L * 1024 * 1024,
                $"streaming exporter must not retain N-row state in memory (delta {delta} bytes)");

            var lines = Encoding.UTF8.GetString(ms.ToArray())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Should().HaveCount(1001, "1000 events + 1 summary");
        }
        finally
        {
            db.Dispose();
            conn.Dispose();
        }
    }
}
