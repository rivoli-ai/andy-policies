// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Enums;

/// <summary>
/// The kind of foreign target a <see cref="Entities.Binding"/> attaches a
/// <see cref="Entities.PolicyVersion"/> to (P3.1, story
/// rivoli-ai/andy-policies#19). The enum is persisted by ordinal to
/// <c>int</c> via EF's <c>HasConversion&lt;int&gt;</c>, so the numeric values
/// MUST stay stable across renames — existing rows on disk depend on
/// <c>1..6</c>.
/// </summary>
public enum BindingTargetType
{
    /// <summary>Template id from andy-tasks (canonical TargetRef shape <c>"template:{guid}"</c>).</summary>
    Template = 1,

    /// <summary>Source repository (canonical TargetRef shape <c>"repo:{org}/{name}"</c>).</summary>
    Repo = 2,

    /// <summary>
    /// Hierarchical scope node (canonical TargetRef shape <c>"scope:{guid}"</c>
    /// where the GUID is <c>ScopeNode.Id</c>). P4.3 joins on the id, not the
    /// materialised path; callers wanting path-based lookup resolve via
    /// <c>IScopeService</c> first.
    /// </summary>
    ScopeNode = 3,

    /// <summary>Tenant id (canonical TargetRef shape <c>"tenant:{guid}"</c>).</summary>
    Tenant = 4,

    /// <summary>Organisation id (canonical TargetRef shape <c>"org:{guid}"</c>).</summary>
    Org = 5,

    /// <summary>
    /// Agent slug from andy-agents (canonical TargetRef shape
    /// <c>"agent:{slug}"</c>). Introduced by SD4.2
    /// (rivoli-ai/andy-policies#1182, parent epic SD #1171) so the default
    /// policy-binding seed can pin per-agent policies (e.g.
    /// <c>coding</c> → <c>write-branch</c>, <c>sandboxed</c>) at org-wide
    /// scope. Agents are flat — there is no hierarchy walk for this
    /// target type — so the tighten-only validator skips it (its
    /// scope-type mapping returns null). Persisted ordinal 6; never
    /// renumber.
    /// </summary>
    Agent = 6,
}
