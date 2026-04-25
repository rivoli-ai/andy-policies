// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;

namespace Andy.Policies.Cli.Http;

/// <summary>
/// Minimal query-string builder that skips null/empty values. Used by list
/// commands to forward optional filters to the REST API without polluting the
/// URL with empty parameters.
/// </summary>
internal static class Querystring
{
    public static string Build(params (string Key, string? Value)[] pairs)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var (key, value) in pairs)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }
            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(Uri.EscapeDataString(key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(value));
        }
        return sb.ToString();
    }
}
