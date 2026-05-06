// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// EF-backed <see cref="IBundleDiffService"/> implementation (P8.6,
/// story rivoli-ai/andy-policies#86). Loads the two bundles' canonical
/// <c>SnapshotJson</c> blobs and walks them as
/// <see cref="JsonElement"/> trees, emitting RFC-6902 ops in
/// deterministic order so two invocations on the same pair produce
/// byte-identical output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism.</b> Object keys are walked in lexicographic
/// order; arrays are walked by index. The canonical bytes from
/// P8.2 already sort keys, so a parsed-then-walked traversal here
/// matches the on-disk shape. The patch JSON itself is emitted
/// without insignificant whitespace.
/// </para>
/// <para>
/// <b>Soft-deleted bundles.</b> Diff requires both bundles to exist
/// and be addressable. Soft-deleted rows return <c>null</c> from
/// the loader so the caller surfaces 404, mirroring the
/// resolution surface (P8.3).
/// </para>
/// </remarks>
public sealed class BundleDiffService : IBundleDiffService
{
    private static readonly JsonWriterOptions PatchWriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };

    private readonly AppDbContext _db;

    public BundleDiffService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<BundleDiffResult?> DiffAsync(
        Guid fromId, Guid toId, CancellationToken ct = default)
    {
        var rows = await _db.Bundles
            .AsNoTracking()
            .Where(b => (b.Id == fromId || b.Id == toId) && b.State == BundleState.Active)
            .Select(b => new { b.Id, b.SnapshotHash, b.SnapshotJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var from = rows.FirstOrDefault(r => r.Id == fromId);
        var to = rows.FirstOrDefault(r => r.Id == toId);
        if (from is null || to is null) return null;

        using var fromDoc = JsonDocument.Parse(from.SnapshotJson);
        using var toDoc = JsonDocument.Parse(to.SnapshotJson);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, PatchWriterOptions))
        {
            writer.WriteStartArray();
            var ops = 0;
            EmitDiff(writer, "", fromDoc.RootElement, toDoc.RootElement, ref ops);
            writer.WriteEndArray();
        }

        var patchJson = Encoding.UTF8.GetString(buffer.ToArray());
        // Re-walk the parsed array to get the op count without
        // tracking it in the writer (the count we tracked above is
        // private). A second parse is cheap on a typical patch.
        using var patchDoc = JsonDocument.Parse(patchJson);
        var opCount = patchDoc.RootElement.GetArrayLength();

        return new BundleDiffResult(
            FromId: fromId,
            FromSnapshotHash: from.SnapshotHash,
            ToId: toId,
            ToSnapshotHash: to.SnapshotHash,
            Rfc6902PatchJson: patchJson,
            OpCount: opCount);
    }

    /// <summary>
    /// Recursively walk <paramref name="from"/> + <paramref name="to"/>
    /// at the same JSON path. Emits one RFC-6902 op per leaf-level
    /// difference; recurses into objects + arrays. <paramref name="ops"/>
    /// counts emitted ops (used for telemetry — not the patch shape).
    /// </summary>
    /// <remarks>
    /// Strategy:
    /// <list type="bullet">
    ///   <item>If kinds differ → <c>replace</c> the whole subtree.</item>
    ///   <item>Object: union the keys (sorted). Key in only one side →
    ///     <c>add</c> / <c>remove</c>. Key in both → recurse.</item>
    ///   <item>Array: index-aligned compare. Trailing extras get
    ///     <c>add</c> at the end (in order); missing tails get
    ///     <c>remove</c> at the same index repeatedly. The
    ///     index-aligned strategy is a deliberate trade-off — it
    ///     produces verbose patches for inserted-in-the-middle
    ///     elements but keeps the algorithm O(n) and deterministic.
    ///     Bundle snapshots already sort by id (P8.2 builder), so
    ///     "insert in the middle" is rare in practice.</item>
    ///   <item>Primitives: structural compare via canonical text;
    ///     differ → <c>replace</c>.</item>
    /// </list>
    /// </remarks>
    private static void EmitDiff(
        Utf8JsonWriter writer,
        string path,
        JsonElement from,
        JsonElement to,
        ref int ops)
    {
        if (from.ValueKind != to.ValueKind)
        {
            EmitOp(writer, "replace", path, to);
            ops++;
            return;
        }

        switch (from.ValueKind)
        {
            case JsonValueKind.Object:
                DiffObjects(writer, path, from, to, ref ops);
                break;
            case JsonValueKind.Array:
                DiffArrays(writer, path, from, to, ref ops);
                break;
            case JsonValueKind.String:
                if (!string.Equals(from.GetString(), to.GetString(), StringComparison.Ordinal))
                {
                    EmitOp(writer, "replace", path, to);
                    ops++;
                }
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                if (!string.Equals(from.GetRawText(), to.GetRawText(), StringComparison.Ordinal))
                {
                    EmitOp(writer, "replace", path, to);
                    ops++;
                }
                break;
        }
    }

    private static void DiffObjects(
        Utf8JsonWriter writer, string path, JsonElement from, JsonElement to, ref int ops)
    {
        var fromKeys = new SortedSet<string>(StringComparer.Ordinal);
        var toKeys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in from.EnumerateObject()) fromKeys.Add(p.Name);
        foreach (var p in to.EnumerateObject()) toKeys.Add(p.Name);

        var allKeys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var k in fromKeys) allKeys.Add(k);
        foreach (var k in toKeys) allKeys.Add(k);

        foreach (var key in allKeys)
        {
            var childPath = path + "/" + EscapePathSegment(key);
            var inFrom = fromKeys.Contains(key);
            var inTo = toKeys.Contains(key);
            if (!inFrom)
            {
                EmitOp(writer, "add", childPath, to.GetProperty(key));
                ops++;
            }
            else if (!inTo)
            {
                EmitOpNoValue(writer, "remove", childPath);
                ops++;
            }
            else
            {
                EmitDiff(writer, childPath, from.GetProperty(key), to.GetProperty(key), ref ops);
            }
        }
    }

    private static void DiffArrays(
        Utf8JsonWriter writer, string path, JsonElement from, JsonElement to, ref int ops)
    {
        var fromLen = from.GetArrayLength();
        var toLen = to.GetArrayLength();
        var common = Math.Min(fromLen, toLen);

        for (var i = 0; i < common; i++)
        {
            EmitDiff(writer, path + "/" + i, from[i], to[i], ref ops);
        }

        // Trailing additions in `to`: emit `add` at the array tail
        // (RFC 6902 supports `path: "/items/-"` to mean append, but
        // an explicit index keeps the patch addressable for tools).
        for (var i = common; i < toLen; i++)
        {
            EmitOp(writer, "add", path + "/" + i, to[i]);
            ops++;
        }

        // Trailing removals in `from`: emit `remove` at the same
        // index repeatedly because each remove shifts the rest down
        // (per RFC 6902 §4.2).
        for (var i = common; i < fromLen; i++)
        {
            EmitOpNoValue(writer, "remove", path + "/" + common);
            ops++;
        }
    }

    private static void EmitOp(Utf8JsonWriter writer, string op, string path, JsonElement value)
    {
        writer.WriteStartObject();
        writer.WriteString("op", op);
        writer.WriteString("path", path);
        writer.WritePropertyName("value");
        value.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static void EmitOpNoValue(Utf8JsonWriter writer, string op, string path)
    {
        writer.WriteStartObject();
        writer.WriteString("op", op);
        writer.WriteString("path", path);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Escape a JSON Pointer path segment per RFC 6901 §3:
    /// '~' → '~0', '/' → '~1'. Done in that order so a literal '~1'
    /// in the key doesn't get double-escaped.
    /// </summary>
    private static string EscapePathSegment(string segment)
        => segment.Replace("~", "~0", StringComparison.Ordinal)
                  .Replace("/", "~1", StringComparison.Ordinal);
}
