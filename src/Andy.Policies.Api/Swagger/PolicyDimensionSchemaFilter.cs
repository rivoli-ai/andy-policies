// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Andy.Policies.Api.Swagger;

/// <summary>
/// Annotates the three string-typed policy-dimension fields
/// (<c>enforcement</c>, <c>severity</c>, <c>state</c>) with the enum union
/// they actually accept on the wire (per ADR 0001 §6). The DTOs hold these
/// as plain <see cref="string"/> so callers can round-trip raw values, but
/// the OpenAPI schema must advertise the closed set so generated clients
/// and Spectral validators reject typos.
/// </summary>
internal sealed class PolicyDimensionSchemaFilter : ISchemaFilter
{
    private static readonly IReadOnlyDictionary<string, string[]> Dimensions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["enforcement"] = new[] { "MUST", "SHOULD", "MAY" },
        ["severity"] = new[] { "info", "moderate", "critical" },
        ["state"] = new[] { "Draft", "Active", "WindingDown", "Retired" },
    };

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema.Properties is null || schema.Properties.Count == 0)
        {
            return;
        }

        foreach (var (name, allowed) in Dimensions)
        {
            if (!schema.Properties.TryGetValue(name, out var prop) || prop.Type != "string")
            {
                continue;
            }
            prop.Enum = allowed.Select(v => (IOpenApiAny)new OpenApiString(v)).ToList();
        }
    }
}
