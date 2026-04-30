// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Enums;

/// <summary>
/// Granularity of an <see cref="Entities.Override"/>'s scope (P5.1,
/// story rivoli-ai/andy-policies#49). <see cref="Principal"/>
/// targets a single subject (e.g. a user or service account);
/// <see cref="Cohort"/> targets a group whose membership is
/// resolved by the consumer at read time (per Epic P5 Non-goals,
/// andy-policies stores the opaque <c>scopeRef</c> and never
/// expands the cohort here).
/// <para>
/// Persisted as <c>string</c> via <c>HasConversion&lt;string&gt;()</c>
/// — the partial-index syntax in <see cref="Entities.Override"/>'s
/// EF config relies on the string form being stable on disk.
/// </para>
/// </summary>
public enum OverrideScopeKind
{
    Principal = 0,
    Cohort = 1,
}
