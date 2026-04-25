// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Text;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.Queries;
using ModelContextProtocol.Server;

namespace Andy.Policies.Api.Mcp;

/// <summary>
/// Read-only MCP tools over the policy catalog (P1.6, story
/// rivoli-ai/andy-policies#76). All five tools delegate to
/// <see cref="IPolicyService"/> — the same service powering the REST surface
/// (P1.5) and gRPC (P1.7) — so wire formats stay consistent across surfaces.
///
/// Mutating tools (create/update/bump) and lifecycle transitions land in
/// later stories: P2.5 publishes, P3.5 bindings, P5.6 overrides, P8.5 bundles.
/// </summary>
[McpServerToolType]
public static class PolicyTools
{
    [McpServerTool(Name = "policy.list"), Description(
        "List policies with optional filters. Filters apply against the active version " +
        "of each policy (highest non-Draft); policies with no active version are excluded " +
        "from filtered results but appear in unfiltered ones. Returns a summary line per " +
        "policy with name, version count, and active version id.")]
    public static async Task<string> ListPolicies(
        IPolicyService policies,
        [Description("Optional case-sensitive prefix on Policy.Name (e.g. 'risk-')")] string? namePrefix = null,
        [Description("Optional exact-match scope membership filter (e.g. 'prod')")] string? scope = null,
        [Description("Optional enforcement filter — MAY / SHOULD / MUST (case-insensitive)")] string? enforcement = null,
        [Description("Optional severity filter — info / moderate / critical (case-insensitive)")] string? severity = null,
        [Description("Pagination offset (default 0)")] int skip = 0,
        [Description("Page size (default 100, capped at 500)")] int take = 100)
    {
        var query = new ListPoliciesQuery(namePrefix, scope, enforcement, severity, skip, take);
        var results = await policies.ListPoliciesAsync(query);

        if (results.Count == 0)
        {
            return "No policies found matching the supplied filters.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{results.Count} polic{(results.Count == 1 ? "y" : "ies")}:");
        foreach (var p in results)
        {
            sb.AppendLine(FormatPolicyLine(p));
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "policy.get"), Description(
        "Get a single policy by id. Returns the stable identity (name, description) plus " +
        "version-history summary (count + active version id, if any). Use " +
        "policy.version.list to enumerate the versions themselves.")]
    public static async Task<string> GetPolicy(
        IPolicyService policies,
        [Description("Policy id (GUID)")] string policyId)
    {
        if (!Guid.TryParse(policyId, out var id))
        {
            return $"Invalid policy id: '{policyId}' is not a valid GUID.";
        }

        var policy = await policies.GetPolicyAsync(id);
        if (policy is null)
        {
            return $"Policy {id} not found.";
        }

        return FormatPolicyDetail(policy);
    }

    [McpServerTool(Name = "policy.version.list"), Description(
        "List all versions of a policy in descending version order (newest first). Each " +
        "line shows version number, lifecycle state, enforcement, severity, and proposer.")]
    public static async Task<string> ListVersions(
        IPolicyService policies,
        [Description("Policy id (GUID)")] string policyId)
    {
        if (!Guid.TryParse(policyId, out var id))
        {
            return $"Invalid policy id: '{policyId}' is not a valid GUID.";
        }

        var versions = await policies.ListVersionsAsync(id);
        if (versions.Count == 0)
        {
            return $"No versions found for policy {id}. Either the policy does not exist or has no versions yet.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{versions.Count} version{(versions.Count == 1 ? "" : "s")} of policy {id}:");
        foreach (var v in versions)
        {
            sb.AppendLine(FormatVersionLine(v));
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "policy.version.get"), Description(
        "Get a specific version of a policy. Returns full content: enforcement, severity, " +
        "scopes, summary, and the rules JSON blob.")]
    public static async Task<string> GetVersion(
        IPolicyService policies,
        [Description("Policy id (GUID)")] string policyId,
        [Description("Version id (GUID) — use policy.version.list to discover")] string versionId)
    {
        if (!Guid.TryParse(policyId, out var pid))
        {
            return $"Invalid policy id: '{policyId}' is not a valid GUID.";
        }
        if (!Guid.TryParse(versionId, out var vid))
        {
            return $"Invalid version id: '{versionId}' is not a valid GUID.";
        }

        var version = await policies.GetVersionAsync(pid, vid);
        if (version is null)
        {
            return $"Version {vid} not found under policy {pid}.";
        }

        return FormatVersionDetail(version);
    }

    [McpServerTool(Name = "policy.version.get-active"), Description(
        "Get the active version of a policy. Active = highest version with State != Draft " +
        "(ADR 0001 §4); returns 'no active version' while every version is still a Draft.")]
    public static async Task<string> GetActiveVersion(
        IPolicyService policies,
        [Description("Policy id (GUID)")] string policyId)
    {
        if (!Guid.TryParse(policyId, out var id))
        {
            return $"Invalid policy id: '{policyId}' is not a valid GUID.";
        }

        var version = await policies.GetActiveVersionAsync(id);
        if (version is null)
        {
            return $"Policy {id} has no active version (every version is still in Draft, or the policy does not exist).";
        }

        return FormatVersionDetail(version);
    }

    // -- formatters ----------------------------------------------------------

    private static string FormatPolicyLine(PolicyDto p)
    {
        var active = p.ActiveVersionId is null
            ? "no active version"
            : $"active version {p.ActiveVersionId}";
        return $"- {p.Name} ({p.Id}) — {p.VersionCount} version{(p.VersionCount == 1 ? "" : "s")}, {active}";
    }

    private static string FormatPolicyDetail(PolicyDto p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Policy: {p.Name}");
        sb.AppendLine($"Id: {p.Id}");
        sb.AppendLine($"Description: {p.Description ?? "(none)"}");
        sb.AppendLine($"Created: {p.CreatedAt:u} by {p.CreatedBySubjectId}");
        sb.AppendLine($"Versions: {p.VersionCount}");
        sb.AppendLine($"Active version: {(p.ActiveVersionId?.ToString() ?? "(none — all drafts)")}");
        return sb.ToString().TrimEnd();
    }

    private static string FormatVersionLine(PolicyVersionDto v) =>
        $"- v{v.Version} ({v.Id}) — {v.State}, {v.Enforcement}/{v.Severity}, proposer={v.ProposerSubjectId}";

    private static string FormatVersionDetail(PolicyVersionDto v)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Version: v{v.Version} of policy {v.PolicyId}");
        sb.AppendLine($"Id: {v.Id}");
        sb.AppendLine($"State: {v.State}");
        sb.AppendLine($"Enforcement: {v.Enforcement}");
        sb.AppendLine($"Severity: {v.Severity}");
        sb.AppendLine($"Scopes: {(v.Scopes.Count == 0 ? "(none)" : string.Join(", ", v.Scopes))}");
        sb.AppendLine($"Summary: {v.Summary}");
        sb.AppendLine($"Created: {v.CreatedAt:u} by {v.CreatedBySubjectId} (proposer: {v.ProposerSubjectId})");
        sb.AppendLine($"Rules:");
        sb.AppendLine(v.RulesJson);
        return sb.ToString().TrimEnd();
    }
}
