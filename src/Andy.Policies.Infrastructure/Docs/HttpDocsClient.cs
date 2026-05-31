// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.Docs;

/// <summary>
/// Production <see cref="IDocsClient"/> for rivoli-ai/conductor#1945
/// (TX F7.2). Issues the andy-docs multipart agent-upload
/// (<c>POST /api/documents:put</c>) via the typed <see cref="HttpClient"/>
/// wired in <c>Program.cs</c> — same Andy.Auth.M2MClient bearer handler
/// the RBAC + tasks clients use. See andy-docs <c>docs/agent-upload.md</c>
/// for the wire contract.
/// </summary>
/// <remarks>
/// The request is <c>multipart/form-data</c> with two parts:
/// <list type="bullet">
///   <item><c>file</c> — the artifact bytes, content type = the document
///     MIME (<c>application/json</c> for the assessment, the NDJSON MIME
///     for the audit-chain export segment).</item>
///   <item><c>meta</c> — a JSON form field carrying
///     <c>{ name, title, message, links[] }</c>. <c>links</c> uses the
///     PascalCase enum wire form (<c>Goal</c>/<c>Task</c>/<c>Audit</c>)
///     per <c>docs-ref-contract.md</c>.</item>
/// </list>
/// The attach is idempotent at andy-docs on
/// <c>(documentId, targetType, targetId, role)</c>, so re-uploading the
/// same assessment for the same <c>(goal, planVersion)</c> re-attaches
/// rather than duplicating. Non-success status codes throw — the
/// best-effort policy lives one layer up in
/// <see cref="Andy.Policies.Application.PlanEvaluation.IComplianceAuditPublisher"/>.
/// </remarks>
public sealed class HttpDocsClient : IDocsClient
{
    // andy-docs links serialise targetType/role as PascalCase strings
    // (closed enums in docs-ref-contract.md). Keep meta JSON camelCase
    // for the field names, but the enum *values* stay PascalCase.
    private static readonly JsonSerializerOptions MetaFormat = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ResponseFormat = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<HttpDocsClient> _log;

    public HttpDocsClient(HttpClient http, ILogger<HttpDocsClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<DocsRef> PutAsync(DocsPutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var form = new MultipartFormDataContent();

        var fileContent = new ByteArrayContent(request.Content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(request.MimeType);
        form.Add(fileContent, "file", request.FileName);

        var meta = new MetaEnvelope(
            Name: request.FileName,
            Title: request.Title,
            Message: request.Message,
            Links: request.Links
                .Select(l => new MetaLink(l.TargetType.ToString(), l.TargetId, l.Role))
                .ToList());
        var metaJson = JsonSerializer.Serialize(meta, MetaFormat);
        var metaContent = new StringContent(metaJson);
        metaContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(metaContent, "meta");

        using var resp = await _http.PostAsync("api/documents:put", form, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // [ANDY-POLICIES-DOCS-PUT-1] non-2xx from andy-docs. Surface
            // status + a bounded body slice so the degraded-audit log line
            // is actionable; the caller maps this to a degraded result.
            var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            throw new DocsPutException(
                $"[ANDY-POLICIES-DOCS-PUT-1] docs.put failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. {body}");
        }

        var dto = await resp.Content.ReadFromJsonAsync<PutResponse>(ResponseFormat, ct)
            .ConfigureAwait(false);
        if (dto is null)
        {
            throw new DocsPutException(
                "[ANDY-POLICIES-DOCS-PUT-2] docs.put returned an empty/undeserialisable body.");
        }

        return new DocsRef(
            DocumentId: dto.DocumentId,
            LinkId: dto.LinkId,
            VersionHash: dto.VersionHash,
            SizeBytes: dto.SizeBytes,
            AdditionalLinkIds: dto.AdditionalLinkIds ?? Array.Empty<Guid>());
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return raw.Length > 512 ? raw[..512] : raw;
        }
        catch
        {
            return "<body unavailable>";
        }
    }

    // ---- wire shapes ------------------------------------------------------

    private sealed record MetaEnvelope(
        string Name,
        string? Title,
        string? Message,
        IReadOnlyList<MetaLink> Links);

    private sealed record MetaLink(string TargetType, string TargetId, string Role);

    private sealed record PutResponse(
        Guid DocumentId,
        Guid LinkId,
        string? VersionHash,
        long SizeBytes,
        IReadOnlyList<Guid>? AdditionalLinkIds);
}

/// <summary>
/// Thrown by <see cref="HttpDocsClient"/> on a non-success
/// <c>docs.put</c>. The <see cref="IComplianceAuditPublisher"/> catches
/// it, logs a source code, and degrades — it never propagates past the
/// publisher so the policy decision is unaffected.
/// </summary>
public sealed class DocsPutException : Exception
{
    public DocsPutException(string message) : base(message)
    {
    }
}
