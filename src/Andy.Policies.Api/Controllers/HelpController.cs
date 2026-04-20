// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// Serves help content from markdown files in content/help/.
/// Consumable by any client: Angular, Swift (Conductor), CLI, MCP.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public partial class HelpController : ControllerBase
{
    private readonly string _helpDir;

    public HelpController(IWebHostEnvironment env, IConfiguration config)
    {
        // Check explicit config first, then resolve from repo root (3 levels up from src/X.Api/)
        var configured = config["Help:ContentPath"];
        if (!string.IsNullOrEmpty(configured))
        {
            _helpDir = Path.GetFullPath(configured, env.ContentRootPath);
        }
        else
        {
            // Development: content/help/ at repo root (../../.. from src/X.Api/)
            var repoRoot = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", ".."));
            _helpDir = Path.Combine(repoRoot, "content", "help");

            // Docker/published: content/help/ next to the dll
            if (!Directory.Exists(_helpDir))
                _helpDir = Path.Combine(env.ContentRootPath, "content", "help");
        }
    }

    /// <summary>
    /// List all help topics (slug, title, order, tags).
    /// </summary>
    [HttpGet("topics")]
    public ActionResult<IEnumerable<HelpTopicSummary>> ListTopics()
    {
        if (!Directory.Exists(_helpDir))
            return Ok(Array.Empty<HelpTopicSummary>());

        var topics = Directory.GetFiles(_helpDir, "*.md")
            .Select(ParseFile)
            .Where(t => t is not null)
            .OrderBy(t => t!.Order)
            .ToList();

        return Ok(topics);
    }

    /// <summary>
    /// Get a single help topic by slug (filename without extension).
    /// Returns title, markdown body, and metadata.
    /// </summary>
    [HttpGet("topics/{slug}")]
    public ActionResult<HelpTopic> GetTopic(string slug)
    {
        // Sanitize slug to prevent path traversal
        if (PathTraversalPattern().IsMatch(slug))
            return BadRequest("Invalid slug");

        var filePath = Path.Combine(_helpDir, $"{slug}.md");
        if (!System.IO.File.Exists(filePath))
            return NotFound();

        var content = System.IO.File.ReadAllText(filePath);
        var (frontMatter, body) = SplitFrontMatter(content);

        return Ok(new HelpTopic(
            Slug: slug,
            Title: ExtractField(frontMatter, "title") ?? slug,
            Order: int.TryParse(ExtractField(frontMatter, "order"), out var o) ? o : 99,
            Tags: ExtractList(frontMatter, "tags"),
            Markdown: body.Trim()
        ));
    }

    /// <summary>
    /// Search help topics by keyword (searches title, tags, and body).
    /// </summary>
    [HttpGet("search")]
    public ActionResult<IEnumerable<HelpTopicSummary>> Search([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(Array.Empty<HelpTopicSummary>());

        if (!Directory.Exists(_helpDir))
            return Ok(Array.Empty<HelpTopicSummary>());

        var query = q.ToLowerInvariant();
        var results = Directory.GetFiles(_helpDir, "*.md")
            .Select(f =>
            {
                var content = System.IO.File.ReadAllText(f);
                var (frontMatter, body) = SplitFrontMatter(content);
                var title = ExtractField(frontMatter, "title") ?? Path.GetFileNameWithoutExtension(f);
                var tags = ExtractList(frontMatter, "tags");
                var slug = Path.GetFileNameWithoutExtension(f);
                var order = int.TryParse(ExtractField(frontMatter, "order"), out var o) ? o : 99;

                var matches = title.ToLowerInvariant().Contains(query)
                    || tags.Any(t => t.ToLowerInvariant().Contains(query))
                    || body.ToLowerInvariant().Contains(query);

                return matches ? new HelpTopicSummary(slug, title, order, tags) : null;
            })
            .Where(t => t is not null)
            .OrderBy(t => t!.Order)
            .ToList();

        return Ok(results);
    }

    private HelpTopicSummary? ParseFile(string filePath)
    {
        var content = System.IO.File.ReadAllText(filePath);
        var (frontMatter, _) = SplitFrontMatter(content);
        var title = ExtractField(frontMatter, "title");
        if (title is null) return null;

        var slug = Path.GetFileNameWithoutExtension(filePath);
        var order = int.TryParse(ExtractField(frontMatter, "order"), out var o) ? o : 99;
        var tags = ExtractList(frontMatter, "tags");

        return new HelpTopicSummary(slug, title, order, tags);
    }

    private static (string frontMatter, string body) SplitFrontMatter(string content)
    {
        if (!content.StartsWith("---"))
            return ("", content);

        var end = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (end < 0) return ("", content);

        var frontMatter = content[3..end].Trim();
        var body = content[(end + 3)..];
        return (frontMatter, body);
    }

    private static string? ExtractField(string frontMatter, string field)
    {
        foreach (var line in frontMatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith($"{field}:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(field.Length + 1)..].Trim().Trim('"');
            }
        }
        return null;
    }

    private static string[] ExtractList(string frontMatter, string field)
    {
        var value = ExtractField(frontMatter, field);
        if (value is null) return Array.Empty<string>();

        // Parse [tag1, tag2, tag3] format
        value = value.Trim('[', ']');
        return value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
    }

    [GeneratedRegex(@"[/\\]|\.\.")]
    private static partial Regex PathTraversalPattern();
}

public record HelpTopicSummary(string Slug, string Title, int Order, string[] Tags);
public record HelpTopic(string Slug, string Title, int Order, string[] Tags, string Markdown);
