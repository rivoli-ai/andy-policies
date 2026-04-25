// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Cli.Output;
using Xunit;

namespace Andy.Policies.Tests.Unit.Cli;

/// <summary>
/// P1.8 acceptance: <c>--output table|json|yaml</c> all produce stable,
/// machine-or-human readable output without throwing. We capture
/// <see cref="Console.Out"/> so the harness asserts on the actual rendered
/// shape rather than just absence-of-crash.
/// </summary>
public class OutputRendererTests
{
    [Fact]
    public void Table_RendersHeadersAndColumnsForArrayOfObjects()
    {
        const string body = "[{\"id\":\"abc\",\"name\":\"read-only\",\"versionCount\":1,\"activeVersionId\":null}]";
        var captured = Capture(() => OutputRenderer.Write(body, "table", new[] { "name", "versionCount" }));

        Assert.Contains("name", captured);
        Assert.Contains("versionCount", captured);
        Assert.Contains("read-only", captured);
        Assert.Contains("1", captured);
        // First data row should not contain the omitted column.
        Assert.DoesNotContain("abc", captured);
    }

    [Fact]
    public void Table_OnEmptyArray_SaysNoRows()
    {
        var captured = Capture(() => OutputRenderer.Write("[]", "table"));
        Assert.Contains("(no rows)", captured);
    }

    [Fact]
    public void Json_PrettyPrintsStableShape()
    {
        const string body = "{\"name\":\"read-only\",\"value\":42}";
        var captured = Capture(() => OutputRenderer.Write(body, "json"));

        Assert.Contains("\"name\"", captured);
        Assert.Contains("\"read-only\"", captured);
        Assert.Contains("42", captured);
        // Pretty-printer must indent — single-line input becomes multi-line.
        Assert.Contains("\n", captured);
    }

    [Fact]
    public void Yaml_RendersScalarAndArrayFields()
    {
        const string body = "{\"name\":\"read-only\",\"scopes\":[\"repo\",\"prod\"]}";
        var captured = Capture(() => OutputRenderer.Write(body, "yaml"));

        Assert.Contains("name:", captured);
        Assert.Contains("read-only", captured);
        Assert.Contains("scopes:", captured);
        Assert.Contains("- repo", captured);
        Assert.Contains("- prod", captured);
    }

    [Fact]
    public void Table_OnObject_RendersFieldValuePairs()
    {
        const string body = "{\"id\":\"abc\",\"name\":\"read-only\"}";
        var captured = Capture(() => OutputRenderer.Write(body, "table"));

        Assert.Contains("field", captured);
        Assert.Contains("value", captured);
        Assert.Contains("name", captured);
        Assert.Contains("read-only", captured);
    }

    private static string Capture(Action action)
    {
        var original = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);
            action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
