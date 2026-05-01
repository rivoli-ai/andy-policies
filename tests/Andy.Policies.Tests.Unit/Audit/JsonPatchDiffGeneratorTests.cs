// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Infrastructure.Audit;
using Andy.Policies.Shared.Auditing;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Audit;

/// <summary>
/// P6.3 (#43) — exercises <see cref="JsonPatchDiffGenerator"/>
/// across the seven shapes the audit chain depends on:
/// <list type="bullet">
///   <item>scalar replace (camelCase path)</item>
///   <item>collection append / replace / remove</item>
///   <item><c>[AuditIgnore]</c> drops the property entirely</item>
///   <item><c>[AuditRedact]</c> emits <c>"***"</c> for the value</item>
///   <item>create (before=null) emits all-add; delete emits all-remove</item>
///   <item>byte stability — re-running on equal inputs produces
///     byte-identical JSON (the hash chain depends on this)</item>
///   <item>RFC 6902 round-trip — output parses through
///     <see cref="JsonDocument"/> and every op has the required
///     <c>op</c>/<c>path</c> properties</item>
/// </list>
/// </summary>
public class JsonPatchDiffGeneratorTests
{
    private sealed class SimpleDto
    {
        public string Name { get; init; } = string.Empty;
        public int Count { get; init; }
        [AuditIgnore]
        public DateTimeOffset ModifiedAt { get; init; }
        [AuditRedact]
        public string? Token { get; init; }
        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    }

    private static readonly JsonSerializerOptions ParseOpts = new(JsonSerializerDefaults.Web);

    private static List<JsonElement> ParseOps(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => JsonDocument.Parse(e.GetRawText()).RootElement)
            .ToList();
    }

    [Fact]
    public void NoChange_ReturnsEmptyArray()
    {
        var gen = new JsonPatchDiffGenerator();
        var dto = new SimpleDto { Name = "p", Count = 1 };

        var patch = gen.GenerateJsonPatch(dto, dto);

        patch.Should().Be("[]");
    }

    [Fact]
    public void ScalarChange_EmitsSingleReplaceWithCamelCasePath()
    {
        var gen = new JsonPatchDiffGenerator();
        var before = new SimpleDto { Name = "alpha", Count = 1 };
        var after = new SimpleDto { Name = "beta", Count = 1 };

        var patch = gen.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().ContainSingle();
        ops[0].GetProperty("op").GetString().Should().Be("replace");
        ops[0].GetProperty("path").GetString().Should().Be("/name");
        ops[0].GetProperty("value").GetString().Should().Be("beta");
    }

    [Fact]
    public void IgnoredProperty_NeverAppearsInPatch()
    {
        var gen = new JsonPatchDiffGenerator();
        var before = new SimpleDto
        {
            Name = "p",
            ModifiedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var after = new SimpleDto
        {
            Name = "p",
            ModifiedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var patch = gen.GenerateJsonPatch(before, after);

        patch.Should().Be("[]", "[AuditIgnore] drops the property entirely");
    }

    [Fact]
    public void RedactedProperty_EmitsTripleAsterisk()
    {
        var gen = new JsonPatchDiffGenerator();
        var before = new SimpleDto { Token = "secret-1" };
        var after = new SimpleDto { Token = "secret-2" };

        var patch = gen.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().ContainSingle();
        ops[0].GetProperty("op").GetString().Should().Be("replace");
        ops[0].GetProperty("path").GetString().Should().Be("/token");
        ops[0].GetProperty("value").GetString().Should().Be("***");
    }

    [Fact]
    public void RedactedProperty_OnAdd_AlsoEmitsTripleAsterisk()
    {
        var gen = new JsonPatchDiffGenerator();
        var before = new SimpleDto();
        var after = new SimpleDto { Token = "secret" };

        var patch = gen.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().ContainSingle(o =>
            o.GetProperty("path").GetString() == "/token");
        ops.Single(o => o.GetProperty("path").GetString() == "/token")
            .GetProperty("value").GetString().Should().Be("***");
    }

    [Fact]
    public void CollectionAppend_EmitsAddAtSlashDash()
    {
        var gen = new JsonPatchDiffGenerator();
        var before = new SimpleDto { Tags = new[] { "a" } };
        var after = new SimpleDto { Tags = new[] { "a", "b" } };

        var patch = gen.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().ContainSingle();
        ops[0].GetProperty("op").GetString().Should().Be("add");
        ops[0].GetProperty("path").GetString().Should().Be("/tags/-");
        ops[0].GetProperty("value").GetString().Should().Be("b");
    }

    [Fact]
    public void CollectionRemove_EmitsRemoveAtIndex()
    {
        var gen = new JsonPatchDiffGenerator();
        var before = new SimpleDto { Tags = new[] { "a", "b", "c" } };
        var after = new SimpleDto { Tags = new[] { "a", "b" } };

        var patch = gen.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().ContainSingle();
        ops[0].GetProperty("op").GetString().Should().Be("remove");
        ops[0].GetProperty("path").GetString().Should().Be("/tags/2");
    }

    [Fact]
    public void CollectionReplace_EmitsReplaceAtIndex()
    {
        var gen = new JsonPatchDiffGenerator();
        var before = new SimpleDto { Tags = new[] { "a", "b" } };
        var after = new SimpleDto { Tags = new[] { "a", "c" } };

        var patch = gen.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().ContainSingle();
        ops[0].GetProperty("op").GetString().Should().Be("replace");
        ops[0].GetProperty("path").GetString().Should().Be("/tags/1");
        ops[0].GetProperty("value").GetString().Should().Be("c");
    }

    [Fact]
    public void BeforeNull_EmitsAddOpsForNonDefaultProperties()
    {
        var gen = new JsonPatchDiffGenerator();
        var after = new SimpleDto { Name = "p", Count = 5, Tags = new[] { "a" } };

        var patch = gen.GenerateJsonPatch<SimpleDto>(null, after);

        var ops = ParseOps(patch);
        ops.Should().AllSatisfy(o =>
            o.GetProperty("op").GetString().Should().Be("add"));
        ops.Select(o => o.GetProperty("path").GetString())
            .Should().Contain(new[] { "/count", "/name", "/tags" });
    }

    [Fact]
    public void AfterNull_EmitsRemoveOpsForAllProperties()
    {
        var gen = new JsonPatchDiffGenerator();
        var before = new SimpleDto { Name = "p", Count = 5, Tags = new[] { "a" } };

        var patch = gen.GenerateJsonPatch<SimpleDto>(before, null);

        var ops = ParseOps(patch);
        ops.Should().AllSatisfy(o =>
            o.GetProperty("op").GetString().Should().Be("remove"));
    }

    [Fact]
    public void OutputIsByteStable_AcrossInvocations()
    {
        // Two invocations on equal inputs must produce
        // byte-identical JSON. The hash chain (P6.2) depends on
        // this — non-stable bytes would corrupt every chain ever
        // written.
        var gen = new JsonPatchDiffGenerator();
        var before = new SimpleDto { Name = "a", Count = 1, Tags = new[] { "x" } };
        var after = new SimpleDto { Name = "b", Count = 2, Tags = new[] { "x", "y" } };

        var first = gen.GenerateJsonPatch(before, after);
        var second = gen.GenerateJsonPatch(before, after);

        first.Should().Be(second);
    }

    [Fact]
    public void OutputIsRfc6902Compliant_OpAndPathAreRequired()
    {
        var gen = new JsonPatchDiffGenerator();
        var before = new SimpleDto { Name = "a", Count = 1, Tags = new[] { "a", "b" } };
        var after = new SimpleDto { Name = "b", Count = 2, Tags = new[] { "a", "b", "c" } };

        var patch = gen.GenerateJsonPatch(before, after);
        using var doc = JsonDocument.Parse(patch);

        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        foreach (var op in doc.RootElement.EnumerateArray())
        {
            op.TryGetProperty("op", out _).Should().BeTrue();
            op.TryGetProperty("path", out var pathProp).Should().BeTrue();
            pathProp.GetString().Should().StartWith("/", "RFC 6902 paths begin with '/'");
        }
    }

    [Fact]
    public void OpsAreSortedByPathThenOp_ForByteStability()
    {
        // Multiple changes — assert lexicographic ordering by path
        // then op so insertion-order changes don't shift the hash.
        var gen = new JsonPatchDiffGenerator();
        var before = new SimpleDto { Name = "a", Count = 1, Tags = new[] { "x" } };
        var after = new SimpleDto { Name = "b", Count = 2, Tags = new[] { "x", "y" } };

        var patch = gen.GenerateJsonPatch(before, after);
        using var doc = JsonDocument.Parse(patch);

        var paths = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("path").GetString()!)
            .ToList();
        paths.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void EnumProperty_DiffsViaWireForm()
    {
        // Ensures the diff respects JsonStringEnumConverter so an
        // enum change emits the wire string ("Active"), not the
        // numeric ordinal.
        var gen = new JsonPatchDiffGenerator();
        var before = new EnumDto { State = TestState.Draft };
        var after = new EnumDto { State = TestState.Active };

        var patch = gen.GenerateJsonPatch(before, after);

        var ops = ParseOps(patch);
        ops.Should().ContainSingle();
        ops[0].GetProperty("value").GetString().Should().Be("Active");
    }

    private sealed class EnumDto
    {
        public TestState State { get; init; }
    }

    private enum TestState
    {
        Draft,
        Active,
        Retired,
    }
}
