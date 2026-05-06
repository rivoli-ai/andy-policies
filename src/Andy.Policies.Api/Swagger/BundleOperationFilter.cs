// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Filters;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Andy.Policies.Api.Swagger;

/// <summary>
/// Swagger operation filter that documents the bundle-pinning gate
/// (P8.4 #84) and the bundle resolution endpoints' HTTP-cache
/// behaviour (P8.3 #83) on the OpenAPI surface (P8.7 #87). Without
/// this filter Swashbuckle would still generate the routes, but the
/// 400 ProblemDetails response and the <c>ETag</c> /
/// <c>Cache-Control</c> headers — both load-bearing for consumers —
/// would be absent from the spec.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pinning gate.</b> Every action carrying
/// <see cref="RequiresBundlePinAttribute"/> gains a documented
/// 400 response with <c>type</c> equal to the stable Problem
/// Details URI (<see cref="BundlePinningFilter.ProblemTypeUri"/>).
/// The <c>bundleId</c> query parameter is not added here because
/// it's already declared on the action signatures with
/// <c>[FromQuery] Guid? bundleId</c> and Swashbuckle picks it up
/// automatically; we only annotate the description so consumers
/// see the gate semantics in the rendered Swagger UI.
/// </para>
/// <para>
/// <b>Cache headers.</b> The bundle resolve and pinned-policy
/// actions emit immutable HTTP-cache headers per P8.3. Adding the
/// headers to the OpenAPI response object lets edge-cache code-
/// generators (Spectral plugins, kubectl-clients, etc.) honour
/// them without sniffing the live response.
/// </para>
/// </remarks>
public sealed class BundleOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        ApplyPinningGateMetadata(operation, context);
        ApplyCacheHeadersMetadata(operation, context);
    }

    private static void ApplyPinningGateMetadata(OpenApiOperation operation, OperationFilterContext context)
    {
        var hasGate = context.MethodInfo
            .GetCustomAttributes(typeof(RequiresBundlePinAttribute), inherit: true)
            .Any();
        if (!hasGate) return;

        // Decorate the existing bundleId parameter (Swashbuckle has
        // already discovered it from the action signature) so the
        // human-readable description names the gate.
        var bundleParam = operation.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "bundleId", StringComparison.OrdinalIgnoreCase));
        if (bundleParam is not null)
        {
            bundleParam.Description =
                "Bundle id to pin the read against. Required when " +
                "andy.policies.bundleVersionPinning is true (the manifest default); " +
                "absence yields a 400 ProblemDetails with type " +
                $"\"{BundlePinningFilter.ProblemTypeUri}\".";
        }

        // Add or augment the 400 response with the Problem Details type URI.
        if (!operation.Responses.TryGetValue("400", out var badRequest))
        {
            badRequest = new OpenApiResponse
            {
                Description = "Bundle pinning required (or other request validation failure).",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = new OpenApiMediaType(),
                },
            };
            operation.Responses["400"] = badRequest;
        }
        if (string.IsNullOrEmpty(badRequest.Description))
        {
            badRequest.Description = "Bundle pinning required (or other request validation failure).";
        }
        // Append the gate's stable type URI as an example so spec
        // consumers can match on it programmatically.
        if (!badRequest.Extensions.ContainsKey("x-andy-pinning-gate-type"))
        {
            badRequest.Extensions["x-andy-pinning-gate-type"] = new OpenApiString(BundlePinningFilter.ProblemTypeUri);
        }
    }

    private static void ApplyCacheHeadersMetadata(OpenApiOperation operation, OperationFilterContext context)
    {
        // The two bundle read endpoints — Resolve + GetPinnedPolicy
        // — emit ETag + Cache-Control on success. Document the
        // headers so generated clients don't drop them on the
        // floor.
        var actionName = context.MethodInfo.Name;
        if (actionName is not "Resolve" && actionName is not "GetPinnedPolicy") return;
        var declaringType = context.MethodInfo.DeclaringType?.Name;
        if (declaringType != "BundlesController") return;

        if (operation.Responses.TryGetValue("200", out var ok))
        {
            ok.Headers["ETag"] = new OpenApiHeader
            {
                Description = "Strong validator: \"<snapshotHash>\" (64 hex chars). " +
                              "Bundles are immutable post-insert (P8.1), so the ETag " +
                              "is stable for the lifetime of the bundle id.",
                Schema = new OpenApiSchema { Type = "string" },
            };
            ok.Headers["Cache-Control"] = new OpenApiHeader
            {
                Description = "public, max-age=31536000, immutable — the response body " +
                              "for a given (bundleId, snapshotHash) never changes.",
                Schema = new OpenApiSchema { Type = "string" },
            };
        }
    }
}
