// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.Settings;
using Andy.Policies.Domain.Enums;
using ModelContextProtocol.Server;

namespace Andy.Policies.Api.Mcp;

/// <summary>
/// MCP tools over the override surface (P5.6, story
/// rivoli-ai/andy-policies#59). Six tools —
/// <c>policy.override.{propose,approve,revoke,list,get,active}</c> —
/// delegate to the same <see cref="IOverrideService"/> powering REST
/// (P5.5, #58) and the upcoming gRPC + CLI surfaces (P5.7). Following
/// the established <see cref="BindingTools"/> contract: string GUIDs
/// (parsed internally), formatted-string returns for human-readable
/// tools, and JSON-serialized envelope for the structured DTO so
/// agents can pipe through deterministic parsers. Mutating tools
/// require an authenticated caller — when the MCP request reaches
/// the tool with no <c>sub</c> / <c>name</c> claim the tool returns
/// an error string rather than writing a fallback subject id into
/// the catalog (mirrors the REST actor-fallback firewall — see #13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Settings gate (P5.4):</b> the three write tools call
/// <see cref="IExperimentalOverridesGate"/> at the top and return
/// <c>policy.override.disabled</c> when the toggle is off — the same
/// stable error code that the REST <c>OverrideWriteGateAttribute</c>
/// surfaces. Reads (<c>list</c>, <c>get</c>, <c>active</c>) bypass
/// the gate so the resolution algorithm (P4.3) keeps working when
/// the toggle is off.
/// </para>
/// <para>
/// <b>Error code stability:</b> each prefixed code in the response
/// matches the REST <c>errorCode</c> extension (P5.5
/// <c>PolicyExceptionHandler</c> additions), so consumers (Conductor
/// admission, Cockpit) can branch on the same strings whether they
/// reach this service via REST, MCP, or gRPC.
/// </para>
/// </remarks>
[McpServerToolType]
public static class OverrideTools
{
    /// <summary>
    /// JSON envelope used by detail/list tools. Mirrors the REST
    /// surface's serializer config (web casing, string enums) so the
    /// MCP-tool body and the REST body are byte-for-byte identical
    /// for agents that fall back to one or the other.
    /// </summary>
    private static readonly JsonSerializerOptions DtoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    [McpServerTool(Name = "policy.override.propose"), Description(
        "Propose a new policy override. ScopeKind is Principal or Cohort; " +
        "Effect is Exempt (no replacement) or Replace (requires " +
        "replacementPolicyVersionId). Returns policy.override.disabled " +
        "when andy.policies.experimentalOverridesEnabled is off.")]
    public static async Task<string> Propose(
        IOverrideService service,
        IExperimentalOverridesGate gate,
        IHttpContextAccessor httpContext,
        [Description("Target policy version id (GUID)")] string policyVersionId,
        [Description("Principal or Cohort")] string scopeKind,
        [Description("Opaque scope ref (e.g. 'user:42', 'cohort:beta-testers'); ≤256 chars")] string scopeRef,
        [Description("Exempt or Replace")] string effect,
        [Description("ISO 8601 timestamp; must be ≥1 minute in the future")] string expiresAt,
        [Description("Required non-empty rationale; ≤2000 chars")] string rationale,
        [Description("Required when effect=Replace; otherwise must be null/empty")] string? replacementPolicyVersionId = null,
        CancellationToken ct = default)
    {
        if (!gate.IsEnabled)
        {
            return "policy.override.disabled: andy.policies.experimentalOverridesEnabled is off.";
        }
        if (!Guid.TryParse(policyVersionId, out var pvid))
        {
            return $"policy.override.invalid_argument: '{policyVersionId}' is not a valid GUID.";
        }
        if (!Enum.TryParse<OverrideScopeKind>(scopeKind, ignoreCase: true, out var sk))
        {
            return $"policy.override.invalid_argument: scopeKind '{scopeKind}' must be Principal or Cohort.";
        }
        if (!Enum.TryParse<OverrideEffect>(effect, ignoreCase: true, out var ef))
        {
            return $"policy.override.invalid_argument: effect '{effect}' must be Exempt or Replace.";
        }
        if (!DateTimeOffset.TryParse(expiresAt, out var exp))
        {
            return $"policy.override.invalid_argument: expiresAt '{expiresAt}' is not a valid ISO 8601 timestamp.";
        }
        Guid? replacementId = null;
        if (!string.IsNullOrEmpty(replacementPolicyVersionId))
        {
            if (!Guid.TryParse(replacementPolicyVersionId, out var rid))
            {
                return $"policy.override.invalid_argument: replacementPolicyVersionId '{replacementPolicyVersionId}' is not a valid GUID.";
            }
            replacementId = rid;
        }

        var actor = ResolveSubjectId(httpContext);
        if (actor is null)
        {
            return "Authentication required: no subject id present on the caller's claims principal.";
        }

        try
        {
            var dto = await service.ProposeAsync(
                new ProposeOverrideRequest(pvid, sk, scopeRef, ef, replacementId, exp, rationale),
                actor, ct);
            return JsonSerializer.Serialize(dto, DtoJsonOptions);
        }
        catch (ValidationException ex)
        {
            return $"policy.override.invalid_argument: {ex.Message}";
        }
        catch (NotFoundException ex)
        {
            return $"policy.override.not_found: {ex.Message}";
        }
    }

    [McpServerTool(Name = "policy.override.approve"), Description(
        "Approve a proposed override. Approver must differ from proposer " +
        "(returns policy.override.self_approval_forbidden otherwise). " +
        "Returns policy.override.invalid_state if the row is past Proposed.")]
    public static async Task<string> Approve(
        IOverrideService service,
        IExperimentalOverridesGate gate,
        IHttpContextAccessor httpContext,
        [Description("Override id (GUID)")] string id,
        CancellationToken ct = default)
    {
        if (!gate.IsEnabled)
        {
            return "policy.override.disabled: andy.policies.experimentalOverridesEnabled is off.";
        }
        if (!Guid.TryParse(id, out var oid))
        {
            return $"policy.override.invalid_argument: '{id}' is not a valid GUID.";
        }

        var actor = ResolveSubjectId(httpContext);
        if (actor is null)
        {
            return "Authentication required: no subject id present on the caller's claims principal.";
        }

        try
        {
            var dto = await service.ApproveAsync(oid, actor, ct);
            return JsonSerializer.Serialize(dto, DtoJsonOptions);
        }
        catch (SelfApprovalException ex)
        {
            return $"policy.override.self_approval_forbidden: {ex.Message}";
        }
        catch (RbacDeniedException ex)
        {
            return $"policy.override.rbac_denied: {ex.Message}";
        }
        catch (NotFoundException ex)
        {
            return $"policy.override.not_found: {ex.Message}";
        }
        catch (ConflictException ex)
        {
            return $"policy.override.invalid_state: {ex.Message}";
        }
    }

    [McpServerTool(Name = "policy.override.revoke"), Description(
        "Revoke an override (Proposed or Approved). Requires a non-empty " +
        "revocationReason. Reaper-driven Expired transitions go through " +
        "P5.3 instead.")]
    public static async Task<string> Revoke(
        IOverrideService service,
        IExperimentalOverridesGate gate,
        IHttpContextAccessor httpContext,
        [Description("Override id (GUID)")] string id,
        [Description("Required non-empty revocation reason; ≤2000 chars")] string revocationReason,
        CancellationToken ct = default)
    {
        if (!gate.IsEnabled)
        {
            return "policy.override.disabled: andy.policies.experimentalOverridesEnabled is off.";
        }
        if (!Guid.TryParse(id, out var oid))
        {
            return $"policy.override.invalid_argument: '{id}' is not a valid GUID.";
        }

        var actor = ResolveSubjectId(httpContext);
        if (actor is null)
        {
            return "Authentication required: no subject id present on the caller's claims principal.";
        }

        try
        {
            var dto = await service.RevokeAsync(oid, new RevokeOverrideRequest(revocationReason), actor, ct);
            return JsonSerializer.Serialize(dto, DtoJsonOptions);
        }
        catch (ValidationException ex)
        {
            return $"policy.override.invalid_argument: {ex.Message}";
        }
        catch (RbacDeniedException ex)
        {
            return $"policy.override.rbac_denied: {ex.Message}";
        }
        catch (NotFoundException ex)
        {
            return $"policy.override.not_found: {ex.Message}";
        }
        catch (ConflictException ex)
        {
            return $"policy.override.invalid_state: {ex.Message}";
        }
    }

    [McpServerTool(Name = "policy.override.list"), Description(
        "List overrides with optional filters: state (Proposed|Approved|" +
        "Revoked|Expired), scopeKind (Principal|Cohort), scopeRef (exact " +
        "match), policyVersionId (GUID). Returns a JSON array of " +
        "OverrideDto records.")]
    public static async Task<string> List(
        IOverrideService service,
        [Description("Proposed | Approved | Revoked | Expired (case-insensitive)")] string? state = null,
        [Description("Principal | Cohort (case-insensitive)")] string? scopeKind = null,
        [Description("Exact-match scope ref")] string? scopeRef = null,
        [Description("Optional policy version id filter (GUID)")] string? policyVersionId = null,
        CancellationToken ct = default)
    {
        OverrideState? stateFilter = null;
        if (!string.IsNullOrEmpty(state))
        {
            if (!Enum.TryParse<OverrideState>(state, ignoreCase: true, out var parsed))
            {
                return $"policy.override.invalid_argument: state '{state}' must be Proposed, Approved, Revoked, or Expired.";
            }
            stateFilter = parsed;
        }
        OverrideScopeKind? scopeFilter = null;
        if (!string.IsNullOrEmpty(scopeKind))
        {
            if (!Enum.TryParse<OverrideScopeKind>(scopeKind, ignoreCase: true, out var parsed))
            {
                return $"policy.override.invalid_argument: scopeKind '{scopeKind}' must be Principal or Cohort.";
            }
            scopeFilter = parsed;
        }
        Guid? pvid = null;
        if (!string.IsNullOrEmpty(policyVersionId))
        {
            if (!Guid.TryParse(policyVersionId, out var parsed))
            {
                return $"policy.override.invalid_argument: policyVersionId '{policyVersionId}' is not a valid GUID.";
            }
            pvid = parsed;
        }

        var rows = await service.ListAsync(
            new OverrideListFilter(stateFilter, scopeFilter, scopeRef, pvid), ct);
        return JsonSerializer.Serialize(rows, DtoJsonOptions);
    }

    [McpServerTool(Name = "policy.override.get"), Description(
        "Get a single override by id. Returns policy.override.not_found " +
        "if the row does not exist; visibility is not state-gated " +
        "(Expired/Revoked rows are still readable for audit).")]
    public static async Task<string> Get(
        IOverrideService service,
        [Description("Override id (GUID)")] string id,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var oid))
        {
            return $"policy.override.invalid_argument: '{id}' is not a valid GUID.";
        }
        var dto = await service.GetAsync(oid, ct);
        return dto is null
            ? $"policy.override.not_found: Override {oid} not found."
            : JsonSerializer.Serialize(dto, DtoJsonOptions);
    }

    [McpServerTool(Name = "policy.override.active"), Description(
        "Currently-effective overrides for (scopeKind, scopeRef): only rows " +
        "where State == Approved AND ExpiresAt > now. Used by Conductor " +
        "during admission and by P4.3 chain resolution. Bypasses the " +
        "experimental-overrides settings gate.")]
    public static async Task<string> Active(
        IOverrideService service,
        [Description("Principal | Cohort")] string scopeKind,
        [Description("Exact-match scope ref")] string scopeRef,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<OverrideScopeKind>(scopeKind, ignoreCase: true, out var sk))
        {
            return $"policy.override.invalid_argument: scopeKind '{scopeKind}' must be Principal or Cohort.";
        }
        if (string.IsNullOrWhiteSpace(scopeRef))
        {
            return "policy.override.invalid_argument: scopeRef is required.";
        }
        var rows = await service.GetActiveAsync(sk, scopeRef, ct);
        return JsonSerializer.Serialize(rows, DtoJsonOptions);
    }

    private static string? ResolveSubjectId(IHttpContextAccessor accessor)
    {
        var user = accessor.HttpContext?.User;
        if (user is null) return null;
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name;
        return string.IsNullOrEmpty(sub) ? null : sub;
    }
}
