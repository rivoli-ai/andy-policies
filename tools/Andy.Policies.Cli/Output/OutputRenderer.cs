// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;

namespace Andy.Policies.Cli.Output;

/// <summary>
/// Renders REST response bodies (always JSON) into the user-selected format.
/// Three formats are supported per Epic AN's shared-flag contract:
/// <list type="bullet">
///   <item><c>table</c> — human-friendly ASCII grid (default)</item>
///   <item><c>json</c>  — pretty-printed JSON for scripts</item>
///   <item><c>yaml</c>  — YAML for configuration-style consumption</item>
/// </list>
/// Tables are intentionally a small custom renderer rather than a heavy
/// dependency: they only need to print a column subset of object fields.
/// </summary>
internal static class OutputRenderer
{
    private static readonly JsonSerializerOptions JsonIndented = new() { WriteIndented = true };

    public static void Write(string body, string format, IReadOnlyList<string>? columns = null)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        switch (format)
        {
            case "json":
                WriteJson(body);
                break;
            case "yaml":
                WriteYaml(body);
                break;
            default:
                WriteTable(body, columns);
                break;
        }
    }

    private static void WriteJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, JsonIndented));
        }
        catch (JsonException)
        {
            Console.WriteLine(body);
        }
    }

    private static void WriteYaml(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var graph = ToObjectGraph(doc.RootElement);
            var yaml = new SerializerBuilder().Build().Serialize(graph);
            Console.Write(yaml);
        }
        catch (JsonException)
        {
            Console.WriteLine(body);
        }
    }

    private static void WriteTable(string body, IReadOnlyList<string>? columns)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            Console.WriteLine(body);
            return;
        }

        using (doc)
        {
            switch (doc.RootElement.ValueKind)
            {
                case JsonValueKind.Array:
                    RenderArrayTable(doc.RootElement, columns);
                    break;
                case JsonValueKind.Object:
                    RenderObjectTable(doc.RootElement, columns);
                    break;
                default:
                    Console.WriteLine(doc.RootElement.ToString());
                    break;
            }
        }
    }

    private static void RenderArrayTable(JsonElement array, IReadOnlyList<string>? columns)
    {
        var rows = new List<JsonElement>();
        foreach (var item in array.EnumerateArray())
        {
            rows.Add(item);
        }
        if (rows.Count == 0)
        {
            Console.WriteLine("(no rows)");
            return;
        }

        IReadOnlyList<string> headers = columns is { Count: > 0 }
            ? columns
            : DiscoverColumns(rows);

        var grid = new List<string[]> { headers.ToArray() };
        foreach (var row in rows)
        {
            var cells = new string[headers.Count];
            for (var i = 0; i < headers.Count; i++)
            {
                cells[i] = row.ValueKind == JsonValueKind.Object && row.TryGetProperty(headers[i], out var prop)
                    ? Stringify(prop)
                    : string.Empty;
            }
            grid.Add(cells);
        }
        WriteGrid(grid);
    }

    private static void RenderObjectTable(JsonElement obj, IReadOnlyList<string>? columns)
    {
        var grid = new List<string[]> { new[] { "field", "value" } };
        if (columns is { Count: > 0 })
        {
            foreach (var key in columns)
            {
                grid.Add(new[]
                {
                    key,
                    obj.TryGetProperty(key, out var prop) ? Stringify(prop) : string.Empty,
                });
            }
        }
        else
        {
            foreach (var prop in obj.EnumerateObject())
            {
                grid.Add(new[] { prop.Name, Stringify(prop.Value) });
            }
        }
        WriteGrid(grid);
    }

    private static IReadOnlyList<string> DiscoverColumns(IReadOnlyList<JsonElement> rows)
    {
        var seen = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            foreach (var prop in row.EnumerateObject())
            {
                if (set.Add(prop.Name))
                {
                    seen.Add(prop.Name);
                }
            }
        }
        return seen;
    }

    private static void WriteGrid(IReadOnlyList<string[]> grid)
    {
        if (grid.Count == 0)
        {
            return;
        }
        var cols = grid[0].Length;
        var widths = new int[cols];
        foreach (var row in grid)
        {
            for (var i = 0; i < cols; i++)
            {
                if (row[i].Length > widths[i])
                {
                    widths[i] = row[i].Length;
                }
            }
        }

        var sb = new StringBuilder();
        for (var rowIdx = 0; rowIdx < grid.Count; rowIdx++)
        {
            var row = grid[rowIdx];
            for (var i = 0; i < cols; i++)
            {
                if (i > 0)
                {
                    sb.Append("  ");
                }
                sb.Append(row[i].PadRight(widths[i]));
            }
            sb.AppendLine();
            if (rowIdx == 0)
            {
                for (var i = 0; i < cols; i++)
                {
                    if (i > 0)
                    {
                        sb.Append("  ");
                    }
                    sb.Append(new string('-', widths[i]));
                }
                sb.AppendLine();
            }
        }
        Console.Write(sb.ToString());
    }

    private static string Stringify(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        JsonValueKind.Array => element.GetRawText(),
        JsonValueKind.Object => element.GetRawText(),
        _ => element.GetRawText(),
    };

    private static object? ToObjectGraph(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in element.EnumerateObject())
                {
                    dict[prop.Name] = ToObjectGraph(prop.Value);
                }
                return dict;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(ToObjectGraph(item));
                }
                return list;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l)
                    ? l
                    : element.GetDouble().ToString(CultureInfo.InvariantCulture);
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            default:
                return element.GetRawText();
        }
    }
}
