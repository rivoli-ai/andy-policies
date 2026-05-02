// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Andy.Policies.Cli.Commands;
using Andy.Policies.Shared.Auditing;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Cli;

/// <summary>
/// P6.5 (#45) — command-tree shape + offline-verification
/// behaviour for <see cref="AuditCommands"/>. The live-mode
/// path is exercised by the integration suite (it needs a real
/// HttpClient against the test server); these tests cover the
/// pure offline NDJSON verifier and the option set.
/// </summary>
public class AuditCommandsTests
{
    private static Command BuildAuditRoot()
    {
        var apiUrl = new Option<string>("--api-url", () => "https://test");
        var token = new Option<string?>("--token");
        var output = new Option<string>("--output", () => "table");
        var root = new Command("audit", "Inspect the catalog audit chain");
        AuditCommands.Register(root, apiUrl, token, output);
        return root;
    }

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    [InlineData("verify")]
    [InlineData("export")]
    public void Audit_RegistersSubcommand(string name)
    {
        var root = BuildAuditRoot();
        root.Subcommands.Select(c => c.Name).Should().Contain(name);
    }

    [Fact]
    public void List_HasFilterOptions_NoneRequired()
    {
        var root = BuildAuditRoot();
        var list = root.Subcommands.First(c => c.Name == "list");

        list.Options.Select(o => o.Name).Should().Contain(new[]
        {
            "actor", "entity-type", "entity-id", "action", "from", "to", "cursor", "page-size",
        });
        list.Options.Where(o => o.IsRequired).Should().BeEmpty();
    }

    [Fact]
    public void Get_RequiresPositionalIdArgument()
    {
        var root = BuildAuditRoot();
        var get = root.Subcommands.First(c => c.Name == "get");

        get.Arguments.Should().ContainSingle().Which.Name.Should().Be("id");
    }

    [Fact]
    public void Export_RequiresOutputFlag()
    {
        var root = BuildAuditRoot();
        var export = root.Subcommands.First(c => c.Name == "export");

        export.Options.Where(o => o.IsRequired).Select(o => o.Name)
            .Should().Contain("out");
        export.Options.Select(o => o.Name).Should().Contain(new[] { "from", "to", "out" });
    }

    [Fact]
    public void Verify_HasFromToFileOptions_NoneRequired()
    {
        var root = BuildAuditRoot();
        var verify = root.Subcommands.First(c => c.Name == "verify");

        verify.Options.Select(o => o.Name).Should().Contain(new[] { "from", "to", "file" });
        verify.Options.Where(o => o.IsRequired).Should().BeEmpty();
    }

    [Fact]
    public async Task Verify_OfflineFile_NotFound_ExitCode2()
    {
        var rootCmd = BuildAuditRoot();
        // System.CommandLine returns exit code 2 by convention for
        // failed file-binding/argument errors. We invoke with a
        // non-existent path that's *valid* as a path string so the
        // command handler runs (FileInfo doesn't fail to bind on
        // missing files); the handler emits its own message.
        var nonexistent = Path.Combine(Path.GetTempPath(), $"never-{Guid.NewGuid():n}.ndjson");
        var result = await rootCmd.InvokeAsync(new[] { "verify", "--file", nonexistent });
        result.Should().Be(2 /* ExitCodes.BadArguments */);
    }

    [Fact]
    public async Task Verify_OfflineFile_ValidChain_ExitCode0()
    {
        // Build a valid 3-event chain to disk via the same hasher
        // the production code uses, then verify offline.
        var path = WriteTempChain(3, tamperAt: null);
        try
        {
            var rootCmd = BuildAuditRoot();
            var result = await rootCmd.InvokeAsync(new[] { "verify", "--file", path });
            result.Should().Be(0 /* ExitCodes.Success */);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Verify_OfflineFile_TamperedChain_ExitCode6()
    {
        // Tamper with row 2's hash byte; the verifier must report
        // divergence and exit 6 (AuditDivergence).
        var path = WriteTempChain(5, tamperAt: 2);
        try
        {
            var rootCmd = BuildAuditRoot();
            var result = await rootCmd.InvokeAsync(new[] { "verify", "--file", path });
            result.Should().Be(6 /* ExitCodes.AuditDivergence */);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Verify_OfflineFile_BoundedRange_RespectsFromTo()
    {
        var path = WriteTempChain(5, tamperAt: null);
        try
        {
            var rootCmd = BuildAuditRoot();
            var result = await rootCmd.InvokeAsync(new[]
            {
                "verify", "--file", path, "--from", "2", "--to", "4",
            });
            result.Should().Be(0 /* ExitCodes.Success */);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Verify_OfflineFile_BundleWithSummaryTrailer_ExitCode0()
    {
        // P6.7 (#48): the audit export bundle format adds a
        // "type":"event" discriminator on each event line and
        // a trailing "type":"summary" line. The CLI must parse
        // both shapes — the summary is metadata, not an event,
        // and must be skipped without flagging divergence.
        var path = WriteTempBundle(count: 3);
        try
        {
            var rootCmd = BuildAuditRoot();
            var result = await rootCmd.InvokeAsync(new[] { "verify", "--file", path });
            result.Should().Be(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Materialises a <paramref name="count"/>-event NDJSON chain on disk
    /// using the production hasher; optionally flips a single byte of the
    /// hash at <paramref name="tamperAt"/> (1-indexed seq).
    /// </summary>
    private static string WriteTempChain(int count, long? tamperAt)
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():n}.ndjson");
        var sb = new StringBuilder();
        var prev = new byte[32];
        for (var i = 1; i <= count; i++)
        {
            var id = Guid.Parse($"00000000-0000-0000-0000-{i:D12}");
            var ts = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(i);
            var hash = AuditEnvelopeHasher.ComputeHash(
                prev, id, ts, "user:test", new[] { "admin" }, "policy.update",
                "Policy", $"00000000-0000-0000-0000-{i:D12}", "[]", $"event #{i}");

            var hashHex = Convert.ToHexString(hash).ToLowerInvariant();
            if (tamperAt is { } t && t == i)
            {
                // Flip the first byte of the hash hex so the reader
                // sees a divergent stored hash. The chain is otherwise
                // pristine — VerifyChain should pinpoint this seq.
                var bytes = Convert.FromHexString(hashHex);
                bytes[0] ^= 0xFF;
                hashHex = Convert.ToHexString(bytes).ToLowerInvariant();
            }

            var dto = new
            {
                id,
                seq = (long)i,
                prevHashHex = Convert.ToHexString(prev).ToLowerInvariant(),
                hashHex,
                timestamp = ts,
                actorSubjectId = "user:test",
                actorRoles = new[] { "admin" },
                action = "policy.update",
                entityType = "Policy",
                entityId = $"00000000-0000-0000-0000-{i:D12}",
                fieldDiffJson = "[]",
                rationale = $"event #{i}",
            };
            sb.AppendLine(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            prev = hash;
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>
    /// Materialises a <paramref name="count"/>-event audit bundle on
    /// disk in the P6.7 export format: every event line carries
    /// <c>"type":"event"</c> and a trailing
    /// <c>"type":"summary"</c> line wraps the bundle. The CLI's
    /// offline verifier must skip the summary line and verify
    /// the events end-to-end.
    /// </summary>
    private static string WriteTempBundle(int count)
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-bundle-{Guid.NewGuid():n}.ndjson");
        var sb = new StringBuilder();
        var prev = new byte[32];
        string? terminalHashHex = null;
        for (var i = 1; i <= count; i++)
        {
            var id = Guid.Parse($"00000000-0000-0000-0000-{i:D12}");
            var ts = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(i);
            var hash = AuditEnvelopeHasher.ComputeHash(
                prev, id, ts, "user:test", new[] { "admin" }, "policy.update",
                "Policy", $"00000000-0000-0000-0000-{i:D12}", "[]", $"event #{i}");

            var hashHex = Convert.ToHexString(hash).ToLowerInvariant();
            terminalHashHex = hashHex;
            var line = new
            {
                type = "event",
                id,
                seq = (long)i,
                prevHashHex = Convert.ToHexString(prev).ToLowerInvariant(),
                hashHex,
                timestamp = ts,
                actorSubjectId = "user:test",
                actorRoles = new[] { "admin" },
                action = "policy.update",
                entityType = "Policy",
                entityId = $"00000000-0000-0000-0000-{i:D12}",
                fieldDiffJson = "[]",
                rationale = $"event #{i}",
            };
            sb.AppendLine(JsonSerializer.Serialize(line, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            prev = hash;
        }
        var summary = new
        {
            type = "summary",
            fromSeq = 1L,
            toSeq = (long)count,
            count = (long)count,
            genesisPrevHashHex = new string('0', 64),
            terminalHashHex = terminalHashHex ?? new string('0', 64),
            exportedAt = DateTimeOffset.UtcNow,
        };
        sb.AppendLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
