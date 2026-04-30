// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using ModelContextProtocol.Server;

namespace Andy.Policies.Api.Mcp;

/// <summary>
/// MCP tools over the scope hierarchy (P4.6, story
/// rivoli-ai/andy-policies#34). Six tools —
/// <c>policy.scope.{list,get,tree,create,delete,effective}</c> —
/// delegate to the same <see cref="IScopeService"/> +
/// <see cref="IBindingResolutionService"/> as REST (P4.5), gRPC, and
/// CLI. Following the established pattern in
/// <see cref="PolicyLifecycleTools"/> and <see cref="BindingTools"/>:
/// string GUID inputs (parsed internally), formatted-string returns
/// for human-readable tools, JSON envelopes for structured reads
/// (tree, effective), and prefixed error codes
/// (<c>policy.scope.{not_found,parent_type_mismatch,ref_conflict,has_descendants,invalid_input}</c>).
/// </summary>
[McpServerToolType]
public static class ScopeTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    [McpServerTool(Name = "policy.scope.list"), Description(
        "List scope nodes with an optional type filter. " +
        "type is one of Org / Tenant / Team / Repo / Template / Run " +
        "(case-insensitive); omit to return the entire catalogue. " +
        "Returns one line per node with id, type, ref, and depth.")]
    public static async Task<string> List(
        IScopeService service,
        [Description("Optional type filter (Org/Tenant/Team/Repo/Template/Run). Omit for all.")] string? type = null,
        CancellationToken ct = default)
    {
        ScopeType? filter = null;
        if (!string.IsNullOrEmpty(type))
        {
            if (!Enum.TryParse<ScopeType>(type, ignoreCase: true, out var parsed))
            {
                return $"policy.scope.invalid_input: type '{type}' is not a valid ScopeType.";
            }
            filter = parsed;
        }

        var rows = await service.ListAsync(filter, ct);
        if (rows.Count == 0)
        {
            return "No scope nodes found.";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"{rows.Count} scope node{(rows.Count == 1 ? "" : "s")}:");
        foreach (var n in rows)
        {
            sb.AppendLine($"- {n.Id} [{n.Type}@{n.Depth}] {n.Ref} ({n.DisplayName})");
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "policy.scope.get"), Description(
        "Get a single scope node by id. Returns formatted detail or " +
        "policy.scope.not_found.")]
    public static async Task<string> Get(
        IScopeService service,
        [Description("Scope node id (GUID).")] string id,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var nodeId))
        {
            return $"policy.scope.invalid_input: '{id}' is not a valid GUID.";
        }
        var dto = await service.GetAsync(nodeId, ct);
        return dto is null
            ? $"policy.scope.not_found: ScopeNode {nodeId} not found."
            : FormatNodeDetail(dto);
    }

    [McpServerTool(Name = "policy.scope.tree"), Description(
        "Return the full scope forest as JSON. Each entry has " +
        "{ node, children[] } and is ordered by Ref ASC at every level.")]
    public static async Task<string> Tree(
        IScopeService service,
        CancellationToken ct = default)
    {
        var forest = await service.GetTreeAsync(ct);
        return JsonSerializer.Serialize(forest, JsonOptions);
    }

    [McpServerTool(Name = "policy.scope.create"), Description(
        "Create a new scope node. parentId is null for a root Org and " +
        "required otherwise. type must follow the canonical " +
        "Org → Tenant → Team → Repo → Template → Run ladder; mismatched " +
        "parent type returns policy.scope.parent_type_mismatch. " +
        "Duplicate (Type, Ref) returns policy.scope.ref_conflict.")]
    public static async Task<string> Create(
        IScopeService service,
        [Description("Parent scope node id (GUID). Empty / 'null' for a root Org.")] string? parentId,
        [Description("Scope type (Org/Tenant/Team/Repo/Template/Run).")] string type,
        [Description("Opaque scope reference (e.g. 'repo:rivoli-ai/conductor').")] string @ref,
        [Description("Human-readable display name.")] string displayName,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<ScopeType>(type, ignoreCase: true, out var scopeType))
        {
            return $"policy.scope.invalid_input: type '{type}' is not a valid ScopeType.";
        }

        Guid? parentGuid = null;
        if (!string.IsNullOrEmpty(parentId)
            && !string.Equals(parentId, "null", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(parentId, out var parsed))
            {
                return $"policy.scope.invalid_input: parentId '{parentId}' is not a valid GUID.";
            }
            parentGuid = parsed;
        }

        try
        {
            var dto = await service.CreateAsync(
                new CreateScopeNodeRequest(parentGuid, scopeType, @ref, displayName), ct);
            return FormatNodeDetail(dto);
        }
        catch (InvalidScopeTypeException ex)
        {
            return $"policy.scope.parent_type_mismatch: {ex.Message}";
        }
        catch (ScopeRefConflictException ex)
        {
            return $"policy.scope.ref_conflict: {ex.Message}";
        }
        catch (NotFoundException ex)
        {
            return $"policy.scope.not_found: {ex.Message}";
        }
        catch (ValidationException ex)
        {
            return $"policy.scope.invalid_input: {ex.Message}";
        }
    }

    [McpServerTool(Name = "policy.scope.delete"), Description(
        "Delete a leaf scope node. Refuses with " +
        "policy.scope.has_descendants when the node still has children.")]
    public static async Task<string> Delete(
        IScopeService service,
        [Description("Scope node id (GUID).")] string id,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var nodeId))
        {
            return $"policy.scope.invalid_input: '{id}' is not a valid GUID.";
        }
        try
        {
            await service.DeleteAsync(nodeId, ct);
            return $"ScopeNode {nodeId} deleted.";
        }
        catch (NotFoundException ex)
        {
            return $"policy.scope.not_found: {ex.Message}";
        }
        catch (ScopeHasDescendantsException ex)
        {
            return $"policy.scope.has_descendants: {ex.Message} (childCount={ex.ChildCount})";
        }
    }

    [McpServerTool(Name = "policy.scope.effective"), Description(
        "Resolve the effective policy set for a scope node using the " +
        "stricter-tightens-only fold from P4.3. Returns JSON envelope " +
        "{ scopeNodeId, policies[] } ordered Mandatory-first then by " +
        "PolicyKey ASC. Returns policy.scope.not_found for unknown ids.")]
    public static async Task<string> Effective(
        IBindingResolutionService resolver,
        [Description("Scope node id (GUID).")] string id,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var nodeId))
        {
            return $"policy.scope.invalid_input: '{id}' is not a valid GUID.";
        }
        try
        {
            var result = await resolver.ResolveForScopeAsync(nodeId, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (NotFoundException ex)
        {
            return $"policy.scope.not_found: {ex.Message}";
        }
    }

    private static string FormatNodeDetail(ScopeNodeDto dto)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ScopeNode {dto.Id}");
        sb.AppendLine($"Type: {dto.Type}");
        sb.AppendLine($"Ref: {dto.Ref}");
        sb.AppendLine($"DisplayName: {dto.DisplayName}");
        sb.AppendLine($"ParentId: {(dto.ParentId?.ToString() ?? "(root)")}");
        sb.AppendLine($"Depth: {dto.Depth}");
        sb.AppendLine($"Path: {dto.MaterializedPath}");
        sb.AppendLine($"Created: {dto.CreatedAt:u}");
        sb.AppendLine($"Updated: {dto.UpdatedAt:u}");
        return sb.ToString().TrimEnd();
    }
}
