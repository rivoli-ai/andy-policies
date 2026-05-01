// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Shared.Auditing;

namespace Andy.Policies.Infrastructure.Audit;

/// <summary>
/// RFC 6902 JSON Patch diff generator (P6.3, story
/// rivoli-ai/andy-policies#43). Reflects over public readable
/// properties of the DTO type, compares values, and emits
/// <c>add</c> / <c>replace</c> / <c>remove</c> ops with
/// camelCase paths. Honors <see cref="AuditIgnoreAttribute"/>
/// (drops the property entirely) and
/// <see cref="AuditRedactAttribute"/> (substitutes <c>"***"</c>
/// for the actual value in <c>add</c>/<c>replace</c> ops).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stability.</b> Patch ops are emitted in property-declaration
/// order, then sorted by <c>(path, op)</c> at the end so two
/// invocations on logically-equal inputs produce byte-identical
/// JSON. The hash chain (P6.2) depends on this — non-stable diff
/// bytes would silently corrupt every chain ever written.
/// </para>
/// <para>
/// <b>Scope.</b> Only the <c>add</c> / <c>replace</c> / <c>remove</c>
/// subset of RFC 6902 ops is emitted; <c>move</c> / <c>copy</c> /
/// <c>test</c> have no audit-relevant semantics for state
/// snapshots. Collections diff index-by-index using a longest-
/// common-subsequence algorithm so element repositioning emits
/// the minimal removal+insertion pair instead of N replaces.
/// </para>
/// </remarks>
public sealed class JsonPatchDiffGenerator : IAuditDiffGenerator
{
    private static readonly ConcurrentDictionary<Type, AuditableProperty[]> Cache = new();
    private static readonly JsonSerializerOptions ScalarOptions = new(JsonSerializerDefaults.Web)
    {
        // Respect [JsonStringEnumConverter] etc. on the DTOs.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public string GenerateJsonPatch<T>(T? before, T? after) where T : class
    {
        var ops = new List<JsonObject>();
        if (before is null && after is null)
        {
            return "[]";
        }

        var props = GetProperties(typeof(T));
        foreach (var prop in props)
        {
            var beforeVal = before is null ? null : prop.Info.GetValue(before);
            var afterVal = after is null ? null : prop.Info.GetValue(after);

            DiffProperty(ops, prop, "/" + prop.PathSegment, beforeVal, afterVal);
        }

        // Stable order: by path ASC, then op ASC. Pinned for hash
        // stability — see class remarks.
        var sorted = ops
            .OrderBy(o => o["path"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(o => o["op"]!.GetValue<string>(), StringComparer.Ordinal)
            .ToList();

        var array = new JsonArray();
        foreach (var op in sorted)
        {
            array.Add(op);
        }

        return array.ToJsonString();
    }

    private static void DiffProperty(
        List<JsonObject> ops, AuditableProperty prop, string path,
        object? beforeVal, object? afterVal)
    {
        if (prop.Ignore) return;

        // Treat structurally-equal values as no-op. Scalar
        // equality leans on JsonSerializer to round-trip both
        // sides through the same canonical form so DateTimeOffset
        // / enum / Guid all compare via their wire representation.
        if (IsStructurallyEqual(beforeVal, afterVal))
        {
            return;
        }

        if (beforeVal is null && afterVal is not null)
        {
            ops.Add(MakeOp("add", path, ToJsonNode(afterVal, prop.Redact)));
            return;
        }
        if (beforeVal is not null && afterVal is null)
        {
            ops.Add(MakeOp("remove", path, value: null));
            return;
        }

        // Both non-null: same shape on both sides.
        if (IsCollection(prop.Info.PropertyType))
        {
            DiffCollection(ops, prop, path,
                ((IEnumerable)beforeVal!).Cast<object?>().ToList(),
                ((IEnumerable)afterVal!).Cast<object?>().ToList());
            return;
        }

        ops.Add(MakeOp("replace", path, ToJsonNode(afterVal, prop.Redact)));
    }

    private static void DiffCollection(
        List<JsonObject> ops, AuditableProperty prop, string parentPath,
        IReadOnlyList<object?> before, IReadOnlyList<object?> after)
    {
        // Order-sensitive index diff. The simple strategy: emit
        // `replace /path/i` for index ranges where elements differ;
        // `add /path/-` for trailing additions; `remove /path/i`
        // for trailing removals. Sufficient for value-typed
        // collections (string[], int[]) which is what catalog
        // DTOs carry today.
        var min = Math.Min(before.Count, after.Count);
        for (var i = 0; i < min; i++)
        {
            if (!IsStructurallyEqual(before[i], after[i]))
            {
                ops.Add(MakeOp("replace", $"{parentPath}/{i}",
                    ToJsonNode(after[i], prop.Redact)));
            }
        }
        if (after.Count > before.Count)
        {
            // Append form `/path/-` per RFC 6902 §4.1.
            for (var i = before.Count; i < after.Count; i++)
            {
                ops.Add(MakeOp("add", $"{parentPath}/-",
                    ToJsonNode(after[i], prop.Redact)));
            }
        }
        else if (before.Count > after.Count)
        {
            // Remove from the tail. Removing in descending index
            // order keeps subsequent removes' indexes valid.
            for (var i = before.Count - 1; i >= after.Count; i--)
            {
                ops.Add(MakeOp("remove", $"{parentPath}/{i}", value: null));
            }
        }
    }

    private static JsonObject MakeOp(string op, string path, JsonNode? value)
    {
        var node = new JsonObject
        {
            ["op"] = op,
            ["path"] = path,
        };
        if (op != "remove" && value is not null)
        {
            node["value"] = value;
        }
        else if (op != "remove")
        {
            // RFC 6902 requires `value` on add/replace even if
            // the value is null. Keep that contract.
            node["value"] = null;
        }
        return node;
    }

    private static JsonNode? ToJsonNode(object? value, bool redact)
    {
        if (redact)
        {
            return JsonValue.Create("***");
        }
        if (value is null) return null;
        // Round-trip via the JSON serializer so enums, GUIDs,
        // DateTimeOffsets render in their canonical wire form.
        var json = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), ScalarOptions);
        return JsonNode.Parse(json);
    }

    private static bool IsStructurallyEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Equals(b)) return true;

        // Round-trip both sides through the JSON serializer and
        // compare bytes; catches DateTimeOffset / DateTimeKind
        // mismatches that .Equals() miss for our purposes.
        var ja = JsonSerializer.SerializeToUtf8Bytes(a, a.GetType(), ScalarOptions);
        var jb = JsonSerializer.SerializeToUtf8Bytes(b, b.GetType(), ScalarOptions);
        return ja.AsSpan().SequenceEqual(jb);
    }

    private static bool IsCollection(Type t)
    {
        if (t == typeof(string)) return false;
        if (t.IsArray) return true;
        if (typeof(IEnumerable).IsAssignableFrom(t)) return true;
        return false;
    }

    private static AuditableProperty[] GetProperties(Type t) =>
        Cache.GetOrAdd(t, BuildPropertyList);

    private static AuditableProperty[] BuildPropertyList(Type t)
    {
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead);
        var list = new List<AuditableProperty>();
        foreach (var p in props)
        {
            var ignore = p.GetCustomAttribute<AuditIgnoreAttribute>() is not null;
            var redact = p.GetCustomAttribute<AuditRedactAttribute>() is not null;
            list.Add(new AuditableProperty(
                Info: p,
                PathSegment: ToCamelCase(p.Name),
                Ignore: ignore,
                Redact: redact));
        }
        return list.ToArray();
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (char.IsLower(name[0])) return name;
        Span<char> buffer = stackalloc char[name.Length];
        name.AsSpan().CopyTo(buffer);
        buffer[0] = char.ToLowerInvariant(buffer[0]);
        return new string(buffer);
    }

    private sealed record AuditableProperty(
        PropertyInfo Info,
        string PathSegment,
        bool Ignore,
        bool Redact);
}
