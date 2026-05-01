// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Andy.Policies.Shared.Auditing;

/// <summary>
/// Canonical JSON serializer for the audit hash envelope (P6.2,
/// story rivoli-ai/andy-policies#42). Produces a deterministic
/// UTF-8 byte sequence for any reachable <see cref="JsonElement"/>
/// or <see cref="object"/> graph: lexicographically sorted object
/// keys, no insignificant whitespace, no BOM, standard JSON string
/// escapes, and stable number formatting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> The canonical bytes are the input to
/// <c>SHA-256(prevHash || canonicalJson(payload))</c> in the audit
/// chain. The audit envelope is a closed shape (Guid id, ISO 8601
/// timestamp, strings, arrays of strings, embedded JSON Patch
/// document) — this implementation exercises only the JCS subset
/// those types reach. ADR 0006 (P6.9, #54) pins the algorithm
/// authoritatively; if richer types are added (floating-point
/// configuration, etc.), this file is the place to extend.
/// </para>
/// <para>
/// <b>Why not a third-party library?</b> The .NET ecosystem has
/// no commonly-trusted RFC 8785 implementation as of 2026-Q2; a
/// 200-line internal helper is auditable and pinned to the
/// fixtures we control. ADR 0006 documents the trade-off.
/// </para>
/// </remarks>
public static class CanonicalJson
{
    /// <summary>
    /// Serialise a <see cref="JsonElement"/> graph to the canonical
    /// UTF-8 byte sequence.
    /// </summary>
    public static byte[] Serialize(JsonElement root)
    {
        using var stream = new MemoryStream();
        Write(stream, root);
        return stream.ToArray();
    }

    /// <summary>
    /// Convenience overload: serialise <paramref name="value"/> via
    /// <see cref="JsonSerializer"/> first, then canonicalise the
    /// resulting <see cref="JsonElement"/>. Avoids a per-call
    /// canonical-aware code path on the writer side; the System
    /// serializer's default settings are fine because we
    /// re-serialise canonically.
    /// </summary>
    public static byte[] SerializeObject<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        using var doc = JsonDocument.Parse(json);
        return Serialize(doc.RootElement);
    }

    private static void Write(Stream stream, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(stream, element);
                break;
            case JsonValueKind.Array:
                WriteArray(stream, element);
                break;
            case JsonValueKind.String:
                WriteString(stream, element.GetString()!);
                break;
            case JsonValueKind.Number:
                WriteNumber(stream, element);
                break;
            case JsonValueKind.True:
                WriteRaw(stream, "true");
                break;
            case JsonValueKind.False:
                WriteRaw(stream, "false");
                break;
            case JsonValueKind.Null:
                WriteRaw(stream, "null");
                break;
            default:
                throw new InvalidOperationException(
                    $"CanonicalJson cannot encode {element.ValueKind}");
        }
    }

    private static void WriteObject(Stream stream, JsonElement element)
    {
        // Lexicographic key ordering (UTF-16 code unit order, matching
        // RFC 8785 §3.2.3). Properties are read once and sorted; the
        // sort is stable on the property name.
        var props = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        stream.WriteByte((byte)'{');
        for (var i = 0; i < props.Count; i++)
        {
            if (i > 0) stream.WriteByte((byte)',');
            WriteString(stream, props[i].Name);
            stream.WriteByte((byte)':');
            Write(stream, props[i].Value);
        }
        stream.WriteByte((byte)'}');
    }

    private static void WriteArray(Stream stream, JsonElement element)
    {
        // Array order is preserved — that's part of the input, not a
        // canonicalisation concern. (RFC 8785 §3.2.4.)
        stream.WriteByte((byte)'[');
        var first = true;
        foreach (var item in element.EnumerateArray())
        {
            if (!first) stream.WriteByte((byte)',');
            first = false;
            Write(stream, item);
        }
        stream.WriteByte((byte)']');
    }

    private static void WriteNumber(Stream stream, JsonElement element)
    {
        // The audit envelope only carries Seq (int64); RFC 8785's
        // floating-point dance (§3.2.2.3) doesn't apply to integers.
        // We try Int64 first; if that fails we fall back to Decimal,
        // then Double — the last falls into a deliberately simple
        // round-trip format. Doubles never appear in the catalog
        // audit envelope today; the path exists for future-proofing.
        if (element.TryGetInt64(out var i))
        {
            WriteRaw(stream, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        if (element.TryGetDecimal(out var dec))
        {
            WriteRaw(stream, dec.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        if (element.TryGetDouble(out var d))
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                throw new InvalidOperationException(
                    "CanonicalJson refuses NaN / ±Infinity per RFC 8785 §3.2.2.3.");
            }
            // "R" round-trip format gives the shortest string that
            // parses back to the same bit pattern.
            WriteRaw(stream, d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        throw new InvalidOperationException(
            $"CanonicalJson cannot encode number with raw text {element.GetRawText()}.");
    }

    private static void WriteString(Stream stream, string value)
    {
        stream.WriteByte((byte)'"');
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(value.Length));
        try
        {
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '"':
                        WriteRaw(stream, "\\\"");
                        break;
                    case '\\':
                        WriteRaw(stream, "\\\\");
                        break;
                    case '\b':
                        WriteRaw(stream, "\\b");
                        break;
                    case '\f':
                        WriteRaw(stream, "\\f");
                        break;
                    case '\n':
                        WriteRaw(stream, "\\n");
                        break;
                    case '\r':
                        WriteRaw(stream, "\\r");
                        break;
                    case '\t':
                        WriteRaw(stream, "\\t");
                        break;
                    default:
                        if (ch < 0x20)
                        {
                            // Escape other C0 control codes per RFC 8259.
                            WriteRaw(stream, "\\u" + ((int)ch).ToString("x4"));
                        }
                        else
                        {
                            // Encode non-ASCII as UTF-8 bytes; RFC 8785
                            // §3.2.2.2 mandates "shortest form" — for
                            // assignable code points that means raw
                            // UTF-8, not \uXXXX escapes.
                            var n = Encoding.UTF8.GetBytes(new[] { ch }, 0, 1, buffer, 0);
                            stream.Write(buffer, 0, n);
                        }
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        stream.WriteByte((byte)'"');
    }

    private static void WriteRaw(Stream stream, string ascii)
    {
        // Helper for ASCII-only literal output (numbers, booleans,
        // escape sequences). Bypasses the encoder for performance.
        for (var i = 0; i < ascii.Length; i++)
        {
            stream.WriteByte((byte)ascii[i]);
        }
    }
}
