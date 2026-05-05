// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Api.Mcp.Authorization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Andy.Policies.Api.Mcp;

/// <summary>
/// MCP tools over the binding catalog (P3.5, story
/// rivoli-ai/andy-policies#23). Four tools — <c>policy.binding.list</c>,
/// <c>policy.binding.create</c>, <c>policy.binding.delete</c>,
/// <c>policy.binding.resolve</c> — delegate to the same
/// <see cref="IBindingService"/> + <see cref="IBindingResolver"/>
/// powering REST (P3.3, P3.4), gRPC (P3.6), and CLI (P3.7). Following the
/// established <see cref="PolicyTools"/> + <see cref="PolicyLifecycleTools"/>
/// contract: string GUIDs (parsed internally), formatted-string returns
/// for human-readable tools, and JSON-serialized envelope for resolve so
/// agents can pipe it through deterministic parsers. Mutating tools
/// require an authenticated caller — when the MCP request reaches the
/// tool with no <c>sub</c> / <c>name</c> claim the tool returns an error
/// string rather than writing a fallback subject id into the catalog
/// (mirrors the REST actor-fallback firewall — see #13).
/// </summary>
[McpServerToolType]
public static class BindingTools
{
    /// <summary>
    /// JSON envelope used by <see cref="Resolve"/>. Mirrors the REST
    /// surface's serializer config (web casing, string enums) so the
    /// MCP-tool body and the REST body are byte-for-byte identical for
    /// agents that fall back to one or the other.
    /// </summary>
    private static readonly JsonSerializerOptions ResolveJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    [McpServerTool(Name = "policy.binding.list"), Description(
        "List bindings attached to a specific policy version. Each line shows " +
        "id, target type/ref, bind strength, created-by, and (when included) " +
        "the deletion tombstone.")]
    public static async Task<string> List(
        IBindingService service,
        [Description("Policy version id (GUID)")] string policyVersionId,
        [Description("Include soft-deleted (tombstoned) bindings")] bool includeDeleted = false,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(policyVersionId, out var vid))
        {
            return $"Invalid policy version id: '{policyVersionId}' is not a valid GUID.";
        }

        var rows = await service.ListByPolicyVersionAsync(vid, includeDeleted, ct);
        return FormatBindingList(rows, header: $"{rows.Count} binding{(rows.Count == 1 ? "" : "s")} on version {vid}:");
    }

    [McpServerTool(Name = "policy.binding.create"), Description(
        "Create a binding linking a policy version to a target. targetType is " +
        "one of: Template, Repo, ScopeNode, Tenant, Org (case-insensitive). " +
        "bindStrength is Mandatory or Recommended. Refuses bindings to Retired " +
        "versions with policy.binding.retired_target.")]
    [RbacGuard("andy-policies:binding:manage")]
    public static async Task<string> Create(
        IBindingService service,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        [Description("Target policy version id (GUID)")] string policyVersionId,
        [Description("One of: Template, Repo, ScopeNode, Tenant, Org")] string targetType,
        [Description("Target reference (e.g. 'template:abc', 'repo:org/name')")] string targetRef,
        [Description("Mandatory or Recommended (default Recommended)")] string bindStrength = "Recommended",
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(policyVersionId, out var vid))
        {
            return $"Invalid policy version id: '{policyVersionId}' is not a valid GUID.";
        }
        if (!Enum.TryParse<BindingTargetType>(targetType, ignoreCase: true, out var tt))
        {
            return $"policy.binding.invalid_target: targetType '{targetType}' is not valid. Use Template, Repo, ScopeNode, Tenant, or Org.";
        }
        if (!Enum.TryParse<BindStrength>(bindStrength, ignoreCase: true, out var bs))
        {
            return $"policy.binding.invalid_target: bindStrength '{bindStrength}' is not valid. Use Mandatory or Recommended.";
        }

        var actor = ResolveSubjectId(httpContext);
        if (actor is null)
        {
            return "Authentication required: no subject id present on the caller's claims principal.";
        }

        try
        {
            await McpRbacGuard.EnsureAsync(rbac, httpContext,
                "andy-policies:binding:manage", $"version:{vid}", ct);
        }
        catch (McpAuthorizationException ex)
        {
            return $"policy.binding.forbidden: {ex.Reason}";
        }

        try
        {
            var dto = await service.CreateAsync(
                new CreateBindingRequest(vid, tt, targetRef, bs), actor, ct);
            return FormatBindingDetail(dto);
        }
        catch (BindingRetiredVersionException ex)
        {
            return $"policy.binding.retired_target: {ex.Message}";
        }
        catch (NotFoundException ex)
        {
            return $"policy.binding.not_found: {ex.Message}";
        }
        catch (ValidationException ex)
        {
            return $"policy.binding.invalid_target: {ex.Message}";
        }
    }

    [McpServerTool(Name = "policy.binding.delete"), Description(
        "Soft-delete a binding. Stamps DeletedAt + DeletedBySubjectId on the " +
        "row; the binding remains for the audit chain. Optional rationale is " +
        "recorded against the audit event. Returns policy.binding.not_found " +
        "if the binding does not exist or is already tombstoned.")]
    [RbacGuard("andy-policies:binding:manage")]
    public static async Task<string> Delete(
        IBindingService service,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        [Description("Binding id (GUID)")] string bindingId,
        [Description("Optional rationale recorded against the audit event")] string? rationale = null,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(bindingId, out var id))
        {
            return $"Invalid binding id: '{bindingId}' is not a valid GUID.";
        }

        var actor = ResolveSubjectId(httpContext);
        if (actor is null)
        {
            return "Authentication required: no subject id present on the caller's claims principal.";
        }

        try
        {
            await McpRbacGuard.EnsureAsync(rbac, httpContext,
                "andy-policies:binding:manage", $"binding:{id}", ct);
        }
        catch (McpAuthorizationException ex)
        {
            return $"policy.binding.forbidden: {ex.Reason}";
        }

        try
        {
            await service.DeleteAsync(id, actor, rationale, ct);
            return $"Binding {id} soft-deleted.";
        }
        catch (NotFoundException ex)
        {
            return $"policy.binding.not_found: {ex.Message}";
        }
    }

    [McpServerTool(Name = "policy.binding.resolve"), Description(
        "Resolve all live bindings for an exact (targetType, targetRef) pair. " +
        "Returns JSON containing target metadata and a list of resolved " +
        "bindings (policy name, version state/dimension fields, scopes, " +
        "bind strength). Retired versions are filtered; same-target/same-" +
        "version duplicates dedup with Mandatory > Recommended. Exact-match " +
        "only — no hierarchy walk; that's P4.")]
    public static async Task<string> Resolve(
        IBindingResolver resolver,
        [Description("One of: Template, Repo, ScopeNode, Tenant, Org")] string targetType,
        [Description("Target reference (e.g. 'template:abc')")] string targetRef,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<BindingTargetType>(targetType, ignoreCase: true, out var tt))
        {
            return $"policy.binding.invalid_target: targetType '{targetType}' is not valid. Use Template, Repo, ScopeNode, Tenant, or Org.";
        }
        if (string.IsNullOrWhiteSpace(targetRef))
        {
            return "policy.binding.invalid_target: targetRef is required.";
        }

        var response = await resolver.ResolveExactAsync(tt, targetRef, ct);
        return JsonSerializer.Serialize(response, ResolveJsonOptions);
    }

    // -- helpers --------------------------------------------------------------

    private static string? ResolveSubjectId(IHttpContextAccessor accessor)
    {
        var user = accessor.HttpContext?.User;
        if (user is null) return null;
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name;
        return string.IsNullOrEmpty(sub) ? null : sub;
    }

    private static string FormatBindingList(IReadOnlyList<BindingDto> rows, string header)
    {
        if (rows.Count == 0)
        {
            return "No bindings.";
        }
        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var b in rows)
        {
            var deletedSuffix = b.DeletedAt is null
                ? string.Empty
                : $" [DELETED at {b.DeletedAt:u} by {b.DeletedBySubjectId ?? "?"}]";
            sb.AppendLine(
                $"- {b.Id} {b.TargetType}={b.TargetRef} ({b.BindStrength}) " +
                $"by {b.CreatedBySubjectId} at {b.CreatedAt:u}{deletedSuffix}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatBindingDetail(BindingDto b)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Binding {b.Id}");
        sb.AppendLine($"PolicyVersionId: {b.PolicyVersionId}");
        sb.AppendLine($"Target: {b.TargetType}={b.TargetRef}");
        sb.AppendLine($"BindStrength: {b.BindStrength}");
        sb.AppendLine($"Created: {b.CreatedAt:u} by {b.CreatedBySubjectId}");
        if (b.DeletedAt is not null)
        {
            sb.AppendLine($"Deleted: {b.DeletedAt:u} by {b.DeletedBySubjectId ?? "?"}");
        }
        return sb.ToString().TrimEnd();
    }
}
