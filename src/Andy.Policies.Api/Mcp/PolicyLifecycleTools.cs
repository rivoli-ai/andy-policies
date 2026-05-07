// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Security.Claims;
using System.Text;
using Andy.Policies.Api.Mcp.Authorization;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Andy.Policies.Api.Mcp;

/// <summary>
/// MCP tools driving lifecycle transitions on a <c>PolicyVersion</c> (P2.5,
/// story rivoli-ai/andy-policies#15). Three tools, all delegating to the
/// shared <see cref="ILifecycleTransitionService"/> from P2.2 — same service
/// powering REST (P2.3), gRPC (P2.6), and CLI (P2.7), so wire behaviour
/// stays identical across surfaces:
/// <list type="bullet">
///   <item><c>policy.version.publish</c> — Draft -&gt; Active shortcut for the
///     dominant LLM use case (auto-supersedes the previous Active in the
///     same DB transaction).</item>
///   <item><c>policy.version.transition</c> — generalised <c>(targetState)</c>
///     form that LLMs can compose into multi-step plans.</item>
///   <item><c>policy.lifecycle.matrix</c> — read-only introspection so an
///     agent can reason about valid transitions before choosing an action.</item>
/// </list>
/// Following the established <see cref="PolicyTools"/> contract: tools take
/// string GUIDs (parsed internally), return formatted strings, and never
/// duplicate state-machine logic. Mutating tools require an authenticated
/// caller — when the MCP request reaches the tool with no <c>sub</c> /
/// <c>name</c> claim, the tool returns an error string rather than writing
/// a fallback subject id into the catalog (mirrors the REST actor-fallback
/// firewall — see #13).
/// </summary>
[McpServerToolType]
public static class PolicyLifecycleTools
{
    [McpServerTool(Name = "policy.version.publish"), Description(
        "Publishes a Draft PolicyVersion (Draft -> Active). Auto-supersedes the previous " +
        "Active version of the same policy in the same DB transaction. Requires a " +
        "non-empty rationale when andy.policies.rationaleRequired is true (default). " +
        "Returns the updated version on success or a human-readable error string.")]
    [RbacGuard("andy-policies:policy:publish")]
    public static Task<string> Publish(
        ILifecycleTransitionService transitions,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        [Description("Policy id (GUID)")] string policyId,
        [Description("PolicyVersion id (GUID) of the Draft to publish")] string versionId,
        [Description("Human-readable reason recorded against the publish event")] string rationale,
        CancellationToken ct = default)
        => TransitionAsync(transitions, httpContext, rbac,
            "andy-policies:policy:publish",
            policyId, versionId,
            LifecycleState.Active.ToString(), rationale, ct);

    [McpServerTool(Name = "policy.version.transition"), Description(
        "Transitions a PolicyVersion to the supplied target state. Allowed transitions: " +
        "Draft -> Active, Active -> WindingDown, Active -> Retired, WindingDown -> Retired. " +
        "Any other (from, to) pair returns INVALID_TRANSITION. targetState is parsed " +
        "case-insensitively; targetState=Draft is rejected because the matrix has no edge " +
        "into Draft. Use policy.lifecycle.matrix to introspect allowed transitions.")]
    [RbacGuard("andy-policies:policy:transition")]
    public static async Task<string> Transition(
        ILifecycleTransitionService transitions,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        [Description("Policy id (GUID)")] string policyId,
        [Description("PolicyVersion id (GUID)")] string versionId,
        [Description("One of: Active, WindingDown, Retired (case-insensitive)")] string targetState,
        [Description("Human-readable reason recorded against the transition event")] string rationale,
        CancellationToken ct = default)
        => await TransitionAsync(transitions, httpContext, rbac,
            "andy-policies:policy:transition",
            policyId, versionId, targetState, rationale, ct);

    [McpServerTool(Name = "policy.lifecycle.matrix"), Description(
        "Returns the canonical lifecycle transition matrix as one rule per line. " +
        "Read-only and side-effect free; safe to call from agent planning loops.")]
    public static string Matrix(ILifecycleTransitionService transitions)
    {
        var rules = transitions.GetMatrix();
        var sb = new StringBuilder();
        sb.AppendLine($"{rules.Count} allowed transition{(rules.Count == 1 ? "" : "s")}:");
        foreach (var r in rules)
        {
            sb.AppendLine($"- {r.From} -> {r.To} ({r.Name})");
        }
        return sb.ToString().TrimEnd();
    }

    // -- shared executor -----------------------------------------------------

    private static async Task<string> TransitionAsync(
        ILifecycleTransitionService transitions,
        IHttpContextAccessor httpContext,
        IRbacChecker rbac,
        string permissionCode,
        string policyId,
        string versionId,
        string targetState,
        string rationale,
        CancellationToken ct)
    {
        if (!Guid.TryParse(policyId, out var pid))
        {
            return $"Invalid policy id: '{policyId}' is not a valid GUID.";
        }
        if (!Guid.TryParse(versionId, out var vid))
        {
            return $"Invalid version id: '{versionId}' is not a valid GUID.";
        }
        if (!Enum.TryParse<LifecycleState>(targetState, ignoreCase: true, out var target)
            || target == LifecycleState.Draft)
        {
            return $"Invalid target state: '{targetState}'. Use Active, WindingDown, or Retired.";
        }

        var subjectId = ResolveSubjectId(httpContext);
        if (subjectId is null)
        {
            // The MCP endpoint is RequireAuthorization()-gated so a missing claim
            // here means the tool was wired without auth (or a custom transport
            // bypassed it). Fail loud rather than write a fallback subject id.
            return "Authentication required: no subject id present on the caller's claims principal.";
        }

        try
        {
            await McpRbacGuard.EnsureAsync(rbac, httpContext,
                permissionCode, $"version:{vid}", ct);
        }
        catch (McpAuthorizationException ex)
        {
            return $"policy.lifecycle.forbidden: {ex.Reason}";
        }

        try
        {
            var dto = await transitions.TransitionAsync(pid, vid, target, rationale, subjectId, ct: ct);
            return FormatVersionDetail(dto);
        }
        catch (RationaleRequiredException ex)
        {
            return $"RATIONALE_REQUIRED: {ex.Message}";
        }
        catch (InvalidLifecycleTransitionException ex)
        {
            return $"INVALID_TRANSITION: {ex.Message} See policy.lifecycle.matrix for allowed transitions.";
        }
        catch (ConcurrentPublishException ex)
        {
            return $"CONFLICT: {ex.Message} Re-fetch the active version and retry.";
        }
        catch (NotFoundException ex)
        {
            return $"NOT_FOUND: {ex.Message}";
        }
        catch (ValidationException ex)
        {
            return $"VALIDATION_ERROR: {ex.Message}";
        }
    }

    private static string? ResolveSubjectId(IHttpContextAccessor accessor)
    {
        var user = accessor.HttpContext?.User;
        if (user is null) return null;
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name;
        return string.IsNullOrEmpty(sub) ? null : sub;
    }

    private static string FormatVersionDetail(PolicyVersionDto v)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Version: v{v.Version} of policy {v.PolicyId}");
        sb.AppendLine($"Id: {v.Id}");
        sb.AppendLine($"State: {v.State}");
        sb.AppendLine($"Enforcement: {v.Enforcement}");
        sb.AppendLine($"Severity: {v.Severity}");
        return sb.ToString().TrimEnd();
    }
}
