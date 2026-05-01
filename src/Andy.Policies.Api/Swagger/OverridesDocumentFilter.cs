// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Andy.Policies.Api.Swagger;

/// <summary>
/// OpenAPI polish for the override surface (P5.8, story
/// rivoli-ai/andy-policies#62). Two additions on top of the
/// controller-derived schema:
/// <list type="number">
///   <item>An <c>Overrides</c> tag with a description that documents
///     the settings-gate posture (default-off, write-only).</item>
///   <item>An <c>x-error-codes</c> extension on every
///     <c>/api/overrides*</c> path operation enumerating the stable
///     <c>errorCode</c> strings the API may return. Generated
///     clients (Cockpit Angular SPA, Conductor) use the extension to
///     branch on errors without parsing English.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The error-code list mirrors the surface-parity contract from
/// P5.5 (<c>PolicyExceptionHandler</c> typed mappings) +
/// P5.6 (MCP prefixed error strings) + P5.7 (gRPC trailers). Every
/// surface returns the same <c>errorCode</c> strings; this filter
/// publishes them as the canonical wire-format documentation.
/// </para>
/// </remarks>
internal sealed class OverridesDocumentFilter : IDocumentFilter
{
    /// <summary>Stable error-code contract for write endpoints.
    /// Mirrors the strings stamped by <c>PolicyExceptionHandler</c>
    /// (<c>override.disabled</c>, <c>override.self_approval_forbidden</c>,
    /// <c>rbac.denied</c>) plus the framework status codes used for
    /// validation / not-found / conflict.</summary>
    private static readonly string[] OverrideErrorCodes =
    {
        "override.disabled",
        "override.self_approval_forbidden",
        "rbac.denied",
        "validation_failed",
        "not_found",
        "invalid_state",
    };

    public void Apply(OpenApiDocument doc, DocumentFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(doc);

        // Replace the Overrides tag description with the gate-posture
        // summary. Swashbuckle synthesises a tag from the controller's
        // [Tags] attribute + XML doc summary; that XML is targeted at
        // contributors reading source, while the OpenAPI consumer
        // wants the operational gate posture (which surface is
        // gated, which surface bypasses).
        const string description =
            "Per-principal or per-cohort experimental policy overrides with " +
            "approver + expiry. Writes (propose / approve / revoke) are gated by " +
            "andy.policies.experimentalOverridesEnabled (default false); reads " +
            "remain available regardless of the toggle so the resolution " +
            "algorithm keeps working when the gate is off.";

        doc.Tags ??= new List<OpenApiTag>();
        var existing = doc.Tags.FirstOrDefault(t => t.Name == "Overrides");
        if (existing is null)
        {
            doc.Tags.Add(new OpenApiTag { Name = "Overrides", Description = description });
        }
        else
        {
            existing.Description = description;
        }

        // x-error-codes extension on every /api/overrides[/...] operation
        // so generated clients can render typed error messages without
        // parsing English.
        var codesArray = new OpenApiArray();
        foreach (var code in OverrideErrorCodes)
        {
            codesArray.Add(new OpenApiString(code));
        }

        foreach (var (path, item) in doc.Paths)
        {
            if (!path.StartsWith("/api/overrides", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var op in item.Operations.Values)
            {
                op.Extensions["x-error-codes"] = codesArray;
            }
        }
    }
}
