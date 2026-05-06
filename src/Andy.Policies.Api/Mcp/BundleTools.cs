// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Api.Mcp.Authorization;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Andy.Policies.Api.Mcp;

/// <summary>
/// MCP tools over the bundle surface (P8.5, story
/// rivoli-ai/andy-policies#85). Five tools —
/// <c>policy.bundle.{create,list,get,resolve,delete}</c> — delegate
/// to the same <see cref="IBundleService"/> + <see cref="IBundleResolver"/>
/// powering REST (P8.3, #83) and the upcoming gRPC + CLI (P8.6).
/// Following the established <see cref="OverrideTools"/> contract:
/// string GUIDs (parsed internally), JSON-serialized envelopes for
/// structured DTOs on success, and stable
/// <c>policy.bundle.{invalid_argument,not_found,conflict,forbidden}</c>
/// error codes on failure so consumers can branch identically across
/// REST / MCP / gRPC.
/// </summary>
/// <remarks>
/// <para>
/// <b>Soft-delete posture (#8 non-goal).</b> <c>policy.bundle.delete</c>
/// flips state to <see cref="BundleState.Deleted"/> via
/// <see cref="IBundleService.SoftDeleteAsync"/>; the row remains in
/// the table for audit-chain integrity. A second delete attempt on
/// an already-tombstoned bundle is idempotent and does <b>not</b>
/// append a second <c>bundle.delete</c> audit event — pinned by
/// integration test in P8.2.
/// </para>
/// <para>
/// <b>Authorization.</b> Mutating tools (<c>create</c>, <c>delete</c>)
/// run <see cref="McpRbacGuard.EnsureAsync"/> at the top of the body
/// and translate denials to <c>policy.bundle.forbidden</c>. Reads
/// (<c>list</c>, <c>get</c>, <c>resolve</c>) are gated only by JWT
/// auth at the MCP edge — same scoping decision as the other
/// <c>*Tools</c> classes.
/// </para>
/// </remarks>
[McpServerToolType]
public static class BundleTools
{
    /// <summary>
    /// JSON envelope for structured DTOs. Mirrors the REST surface's
    /// serializer config (web casing, string enums) so the MCP-tool
    /// body and the REST body are byte-for-byte identical for agents
    /// that fall back to one or the other.
    /// </summary>
    private static readonly JsonSerializerOptions DtoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    [McpServerTool(Name = "policy.bundle.create"), Description(
        "Create a new bundle — a frozen snapshot of active policies, " +
        "bindings, and approved overrides. Returns JSON DTO with " +
        "snapshotHash; appends a bundle.create event to the audit chain. " +
        "Returns policy.bundle.invalid_argument for slug/rationale " +
        "violations and policy.bundle.conflict for duplicate active names.")]
    [RbacGuard("andy-policies:bundle:create")]
    public static async Task<string> Create(
        IBundleService service,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        [Description("Slug name; ^[a-z0-9][a-z0-9-]{0,62}$")] string name,
        [Description("Required non-empty rationale captured in the audit chain")] string rationale,
        [Description("Optional human-readable description")] string? description = null,
        CancellationToken ct = default)
    {
        var actor = ResolveSubjectId(httpContext);
        if (actor is null)
        {
            return "Authentication required: no subject id present on the caller's claims principal.";
        }

        try
        {
            await McpRbacGuard.EnsureAsync(rbac, httpContext,
                "andy-policies:bundle:create", $"name:{name}", ct);
        }
        catch (McpAuthorizationException ex)
        {
            return $"policy.bundle.forbidden: {ex.Reason}";
        }

        try
        {
            var dto = await service.CreateAsync(
                new CreateBundleRequest(name, description, rationale), actor, ct);
            return JsonSerializer.Serialize(dto, DtoJsonOptions);
        }
        catch (ValidationException ex)
        {
            return $"policy.bundle.invalid_argument: {ex.Message}";
        }
        catch (ConflictException ex)
        {
            return $"policy.bundle.conflict: {ex.Message}";
        }
    }

    [McpServerTool(Name = "policy.bundle.list"), Description(
        "List bundles. Active bundles by default; pass " +
        "includeDeleted=true to include soft-deleted rows. take is " +
        "clamped to [1, 200]. Returns a JSON array of BundleDto.")]
    public static async Task<string> List(
        IBundleService service,
        [Description("Include soft-deleted bundles in the result (default false).")] bool includeDeleted = false,
        [Description("Pagination skip (default 0)")] int skip = 0,
        [Description("Pagination take; clamped to [1, 200] (default 50)")] int take = 50,
        CancellationToken ct = default)
    {
        var clampedTake = Math.Clamp(take, 1, 200);
        var clampedSkip = Math.Max(0, skip);
        var rows = await service.ListAsync(
            new ListBundlesFilter(includeDeleted, clampedSkip, clampedTake), ct);
        return JsonSerializer.Serialize(rows, DtoJsonOptions);
    }

    [McpServerTool(Name = "policy.bundle.get"), Description(
        "Get a bundle by id. Returns the JSON DTO or " +
        "policy.bundle.not_found when the bundle does not exist.")]
    public static async Task<string> Get(
        IBundleService service,
        [Description("Bundle id (GUID)")] string bundleId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(bundleId, out var bid))
        {
            return $"policy.bundle.invalid_argument: '{bundleId}' is not a valid GUID.";
        }
        var dto = await service.GetAsync(bid, ct);
        return dto is null
            ? $"policy.bundle.not_found: bundle {bid} does not exist."
            : JsonSerializer.Serialize(dto, DtoJsonOptions);
    }

    [McpServerTool(Name = "policy.bundle.resolve"), Description(
        "Resolve bindings for (targetType, targetRef) against a frozen " +
        "bundle snapshot. targetType is one of Template, Repo, " +
        "ScopeNode, Tenant, Org. Returns BundleResolveResult JSON; " +
        "soft-deleted bundle returns policy.bundle.not_found.")]
    public static async Task<string> Resolve(
        IBundleResolver resolver,
        [Description("Bundle id (GUID)")] string bundleId,
        [Description("One of Template, Repo, ScopeNode, Tenant, Org")] string targetType,
        [Description("Target reference, e.g. 'repo:rivoli-ai/conductor'")] string targetRef,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(bundleId, out var bid))
        {
            return $"policy.bundle.invalid_argument: '{bundleId}' is not a valid GUID.";
        }
        if (!Enum.TryParse<BindingTargetType>(targetType, ignoreCase: true, out var tt))
        {
            return $"policy.bundle.invalid_argument: targetType '{targetType}' is not valid.";
        }
        if (string.IsNullOrWhiteSpace(targetRef))
        {
            return "policy.bundle.invalid_argument: targetRef is required.";
        }

        var result = await resolver.ResolveAsync(bid, tt, targetRef, ct);
        return result is null
            ? $"policy.bundle.not_found: bundle {bid} does not exist or is soft-deleted."
            : JsonSerializer.Serialize(result, DtoJsonOptions);
    }

    [McpServerTool(Name = "policy.bundle.delete"), Description(
        "Soft-delete a bundle (state flip to Deleted + tombstone " +
        "stamp). Bundles are never hard-deleted — the row remains " +
        "for audit-chain integrity. Idempotent: a second delete on " +
        "the same id returns policy.bundle.not_found and does not " +
        "append a duplicate audit event.")]
    [RbacGuard("andy-policies:bundle:delete")]
    public static async Task<string> Delete(
        IBundleService service,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        [Description("Bundle id (GUID)")] string bundleId,
        [Description("Required non-empty rationale captured in the audit chain")] string rationale,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(bundleId, out var bid))
        {
            return $"policy.bundle.invalid_argument: '{bundleId}' is not a valid GUID.";
        }

        var actor = ResolveSubjectId(httpContext);
        if (actor is null)
        {
            return "Authentication required: no subject id present on the caller's claims principal.";
        }

        try
        {
            await McpRbacGuard.EnsureAsync(rbac, httpContext,
                "andy-policies:bundle:delete", $"bundle:{bid}", ct);
        }
        catch (McpAuthorizationException ex)
        {
            return $"policy.bundle.forbidden: {ex.Reason}";
        }

        try
        {
            var deleted = await service.SoftDeleteAsync(bid, actor, rationale, ct);
            if (!deleted)
            {
                // Idempotent: no row, or already-Deleted. Distinguish
                // for the caller — we can re-fetch to provide the
                // post-state DTO when the row exists, or return
                // not_found when it does not.
                var existing = await service.GetAsync(bid, ct);
                return existing is null
                    ? $"policy.bundle.not_found: bundle {bid} does not exist."
                    : $"policy.bundle.not_found: bundle {bid} is already soft-deleted.";
            }

            var dto = await service.GetAsync(bid, ct);
            return JsonSerializer.Serialize(dto, DtoJsonOptions);
        }
        catch (ValidationException ex)
        {
            return $"policy.bundle.invalid_argument: {ex.Message}";
        }
    }

    private static string? ResolveSubjectId(IHttpContextAccessor accessor)
    {
        var user = accessor.HttpContext?.User;
        if (user is null) return null;
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name;
        return string.IsNullOrEmpty(sub) ? null : sub;
    }
}
