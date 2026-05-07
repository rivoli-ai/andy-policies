// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Andy.Policies.Api.Controllers;

/// <summary>
/// P9 follow-up #191 (2026-05-07) — JSON Schema served from the API so
/// editors (the Angular policy editor, P9.2; the Monaco swap-in P9.8
/// follow-up #192) can wire schema-aware validation without hard-coding
/// the contract. Anonymous read; trivially cacheable.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this schema actually enforces.</b> Today
/// <c>PolicyService.ValidateRulesJson</c> performs only two checks:
/// (1) the payload parses as JSON and (2) the serialized byte count is
/// ≤ 64KB (ADR 0001 §5). The schema therefore intentionally describes
/// a permissive shape — <c>{"type": "object"}</c> with
/// <c>additionalProperties: true</c> — and surfaces the byte cap via a
/// <c>maxLength</c> hint on the wrapper.
/// </para>
/// <para>
/// <b>Why publish anything if the server is permissive?</b> Two reasons.
/// First, downstream IDE tooling (Monaco's <c>setDiagnosticsOptions</c>)
/// expects a JSON Schema document at a stable URL; serving the
/// permissive draft now lets the editor wire the integration plumbing
/// today and tighten the schema in lockstep with future server-side
/// rules-engine work. Second, declaring "no structural constraints"
/// publicly is itself useful documentation — it tells consumers their
/// rules are opaque to the catalog.
/// </para>
/// <para>
/// <b>Caching contract.</b> The schema is byte-stable across a single
/// build (it's a constant), so the response carries a strong ETag
/// (sha256 of the body, hex) and <c>Cache-Control: public, max-age=300</c>.
/// Conditional GET (<c>If-None-Match</c>) returns 304.
/// </para>
/// </remarks>
[ApiController]
[Route("api/schemas")]
[AllowAnonymous]
[Tags("Schemas")]
public sealed class SchemasController : ControllerBase
{
    /// <summary>
    /// JSON Schema for the rules DSL. Stable across a build — bytes
    /// hashed once at startup (singleton instance).
    /// </summary>
    private const string RulesSchemaJson = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://andy-policies/schemas/rules.json",
          "title": "Andy Policies — Rules DSL",
          "description": "Per-version `rulesJson` payload. Server-side validation today enforces only (1) valid JSON and (2) ≤ 64 KiB serialized size per ADR 0001 §5. Structural constraints are intentionally absent — consumers may emit any JSON object the runtime engine consumes. Future iterations will tighten this in lockstep with formal rules-engine work; the schema URL and `$id` remain stable.",
          "type": "object",
          "additionalProperties": true,
          "x-andy-policies-max-bytes": 65536
        }
        """;

    private static readonly byte[] RulesSchemaBytes =
        Encoding.UTF8.GetBytes(RulesSchemaJson);

    /// <summary>
    /// Strong ETag derived from the schema bytes. Hex-encoded SHA-256 — the
    /// same encoding the audit chain (P6) uses for hash payloads.
    /// </summary>
    private static readonly string RulesSchemaEtag =
        "\"" + Convert.ToHexString(SHA256.HashData(RulesSchemaBytes)).ToLowerInvariant() + "\"";

    /// <summary>
    /// Returns the JSON Schema describing <c>PolicyVersion.rulesJson</c>.
    /// </summary>
    /// <response code="200">Schema body. Strong ETag + 5-minute Cache-Control.</response>
    /// <response code="304">Body unchanged since the supplied <c>If-None-Match</c>.</response>
    [HttpGet("rules.json")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult GetRulesSchema()
    {
        if (Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatch)
            && ifNoneMatch.Any(v => string.Equals(v, RulesSchemaEtag, StringComparison.Ordinal)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.ETag = RulesSchemaEtag;
        Response.Headers.CacheControl = "public, max-age=300";
        return Content(RulesSchemaJson, MediaTypeNames.Application.Json, Encoding.UTF8);
    }
}
