// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Andy.Policies.Api.Mcp;

[McpServerToolType]
public static class HelpTools
{
    private static string ResolveHelpDir(IWebHostEnvironment env)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", ".."));
        var dir = Path.Combine(repoRoot, "content", "help");
        if (!Directory.Exists(dir))
            dir = Path.Combine(env.ContentRootPath, "content", "help");
        return dir;
    }

    [McpServerTool, Description("List all available help topics for this service")]
    public static Task<string> ListHelpTopics(IWebHostEnvironment env)
    {
        var helpDir = ResolveHelpDir(env);
        if (!Directory.Exists(helpDir))
            return Task.FromResult("No help topics available.");

        var files = Directory.GetFiles(helpDir, "*.md").OrderBy(f => f).ToList();
        if (files.Count == 0)
            return Task.FromResult("No help topics available.");

        var topics = files.Select(f =>
        {
            var slug = Path.GetFileNameWithoutExtension(f);
            var content = File.ReadAllText(f);
            var title = ExtractTitle(content) ?? slug;
            return $"- {title} (slug: {slug})";
        });

        return Task.FromResult($"Help topics:\n{string.Join("\n", topics)}");
    }

    [McpServerTool, Description("Get the content of a help topic by its slug (e.g., 'getting-started', 'api-access', 'authentication')")]
    public static Task<string> GetHelpTopic(
        IWebHostEnvironment env,
        [Description("The help topic slug (filename without .md extension)")] string slug)
    {
        var helpDir = ResolveHelpDir(env);
        var filePath = Path.Combine(helpDir, $"{slug}.md");

        if (!File.Exists(filePath))
            return Task.FromResult($"Help topic '{slug}' not found. Use ListHelpTopics to see available topics.");

        var content = File.ReadAllText(filePath);

        // Strip front matter for cleaner output
        if (content.StartsWith("---"))
        {
            var end = content.IndexOf("---", 3, StringComparison.Ordinal);
            if (end >= 0)
                content = content[(end + 3)..].Trim();
        }

        return Task.FromResult(content);
    }

    [McpServerTool, Description("Search help topics by keyword")]
    public static Task<string> SearchHelp(
        IWebHostEnvironment env,
        [Description("Search keyword")] string query)
    {
        var helpDir = ResolveHelpDir(env);
        if (!Directory.Exists(helpDir))
            return Task.FromResult("No help topics available.");

        var q = query.ToLowerInvariant();
        var matches = Directory.GetFiles(helpDir, "*.md")
            .Where(f => File.ReadAllText(f).ToLowerInvariant().Contains(q))
            .Select(f =>
            {
                var slug = Path.GetFileNameWithoutExtension(f);
                var title = ExtractTitle(File.ReadAllText(f)) ?? slug;
                return $"- {title} (slug: {slug})";
            })
            .ToList();

        if (matches.Count == 0)
            return Task.FromResult($"No help topics match '{query}'.");

        return Task.FromResult($"Matching topics:\n{string.Join("\n", matches)}");
    }

    private static string? ExtractTitle(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                return trimmed[6..].Trim().Trim('"');
        }
        return null;
    }
}
