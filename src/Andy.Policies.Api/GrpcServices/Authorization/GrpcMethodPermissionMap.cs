// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Api.GrpcServices.Authorization;

/// <summary>
/// Authoritative mapping of every enforced gRPC method to its
/// <c>andy-policies:…</c> permission code. P7.6 (#64).
/// </summary>
/// <remarks>
/// <para>
/// The package prefix in the keys (<c>"/andy_policies."</c>) matches
/// the proto <c>package andy_policies;</c> declaration; the service
/// segment matches <c>service XxxService { … }</c>; the method segment
/// matches the rpc name.
/// </para>
/// <para>
/// <b>Items service is intentionally absent.</b> It ships as template
/// scaffolding and is not a governance surface — the
/// <see cref="RbacServerInterceptor"/> bypasses it via the
/// <see cref="IsEnforcedService"/> allowlist below. Every other
/// service must have every rpc mapped here, which is asserted by a
/// reflection-based coverage test in the integration project.
/// </para>
/// </remarks>
public sealed class GrpcMethodPermissionMap : IGrpcMethodPermissionMap
{
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        // PolicyService — reads + drafting
        ["/andy_policies.PolicyService/ListPolicies"]      = "andy-policies:policy:read",
        ["/andy_policies.PolicyService/GetPolicy"]         = "andy-policies:policy:read",
        ["/andy_policies.PolicyService/GetPolicyByName"]   = "andy-policies:policy:read",
        ["/andy_policies.PolicyService/ListVersions"]      = "andy-policies:policy:read",
        ["/andy_policies.PolicyService/GetVersion"]        = "andy-policies:policy:read",
        ["/andy_policies.PolicyService/GetActiveVersion"]  = "andy-policies:policy:read",
        ["/andy_policies.PolicyService/CreateDraft"]       = "andy-policies:policy:author",
        ["/andy_policies.PolicyService/UpdateDraft"]       = "andy-policies:policy:author",
        ["/andy_policies.PolicyService/BumpDraft"]         = "andy-policies:policy:author",

        // LifecycleService — publish + wind-down + retire + matrix
        ["/andy_policies.LifecycleService/PublishVersion"]    = "andy-policies:policy:publish",
        ["/andy_policies.LifecycleService/TransitionVersion"] = "andy-policies:policy:transition",
        ["/andy_policies.LifecycleService/GetMatrix"]         = "andy-policies:policy:read",

        // BindingService
        ["/andy_policies.BindingService/CreateBinding"]                = "andy-policies:binding:manage",
        ["/andy_policies.BindingService/DeleteBinding"]                = "andy-policies:binding:manage",
        ["/andy_policies.BindingService/GetBinding"]                   = "andy-policies:binding:read",
        ["/andy_policies.BindingService/ListBindingsByPolicyVersion"]  = "andy-policies:binding:read",
        ["/andy_policies.BindingService/ListBindingsByTarget"]         = "andy-policies:binding:read",
        ["/andy_policies.BindingService/ResolveBindings"]              = "andy-policies:binding:read",

        // ScopesService
        ["/andy_policies.ScopesService/ListScopes"]            = "andy-policies:scope:read",
        ["/andy_policies.ScopesService/GetScope"]              = "andy-policies:scope:read",
        ["/andy_policies.ScopesService/GetScopeTree"]          = "andy-policies:scope:read",
        ["/andy_policies.ScopesService/GetEffectivePolicies"]  = "andy-policies:scope:read",
        ["/andy_policies.ScopesService/CreateScope"]           = "andy-policies:scope:manage",
        ["/andy_policies.ScopesService/DeleteScope"]           = "andy-policies:scope:manage",

        // OverridesService
        ["/andy_policies.OverridesService/ProposeOverride"]   = "andy-policies:override:propose",
        ["/andy_policies.OverridesService/ApproveOverride"]   = "andy-policies:override:approve",
        ["/andy_policies.OverridesService/RevokeOverride"]    = "andy-policies:override:revoke",
        ["/andy_policies.OverridesService/ListOverrides"]     = "andy-policies:override:read",
        ["/andy_policies.OverridesService/GetOverride"]       = "andy-policies:override:read",
        ["/andy_policies.OverridesService/GetActiveOverrides"]= "andy-policies:override:read",

        // AuditService
        ["/andy_policies.AuditService/ListAudit"]    = "andy-policies:audit:read",
        ["/andy_policies.AuditService/GetAudit"]     = "andy-policies:audit:read",
        ["/andy_policies.AuditService/VerifyAudit"]  = "andy-policies:audit:verify",
        ["/andy_policies.AuditService/ExportAudit"]  = "andy-policies:audit:export",

        // BundleService — P8.6 (#86) parity over IBundleService /
        // IBundleResolver / IBundleDiffService. Diff is treated as
        // a read (it's a snapshot comparison; no mutation).
        ["/andy_policies.BundleService/CreateBundle"]  = "andy-policies:bundle:create",
        ["/andy_policies.BundleService/ListBundles"]   = "andy-policies:bundle:read",
        ["/andy_policies.BundleService/GetBundle"]     = "andy-policies:bundle:read",
        ["/andy_policies.BundleService/ResolveBundle"] = "andy-policies:bundle:read",
        ["/andy_policies.BundleService/DeleteBundle"]  = "andy-policies:bundle:delete",
        ["/andy_policies.BundleService/DiffBundles"]   = "andy-policies:bundle:read",
    };

    /// <summary>Services not in this set are allowed to bypass RBAC.
    /// Currently only the template-scaffolding ItemsService.</summary>
    private static readonly HashSet<string> EnforcedServices = new(StringComparer.Ordinal)
    {
        "/andy_policies.PolicyService",
        "/andy_policies.LifecycleService",
        "/andy_policies.BindingService",
        "/andy_policies.ScopesService",
        "/andy_policies.OverridesService",
        "/andy_policies.AuditService",
        "/andy_policies.BundleService",
    };

    public IReadOnlyDictionary<string, string> Entries => Map;

    public bool TryGetPermission(string fullyQualifiedMethod, out string permissionCode)
    {
        if (Map.TryGetValue(fullyQualifiedMethod, out var code))
        {
            permissionCode = code;
            return true;
        }
        permissionCode = string.Empty;
        return false;
    }

    public static bool IsEnforcedService(string fullyQualifiedMethod)
    {
        var slash = fullyQualifiedMethod.LastIndexOf('/');
        if (slash <= 0) return false;
        var serviceSegment = fullyQualifiedMethod[..slash];
        return EnforcedServices.Contains(serviceSegment);
    }
}
