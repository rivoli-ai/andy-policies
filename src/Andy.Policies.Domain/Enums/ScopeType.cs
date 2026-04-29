// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Enums;

/// <summary>
/// Position of a <see cref="Entities.ScopeNode"/> in the six-level
/// hierarchy <c>Org → Tenant → Team → Repo → Template → Run</c>
/// (P4.1, story rivoli-ai/andy-policies#28). Persisted by ordinal to
/// <c>int</c> via EF's <c>HasConversion&lt;int&gt;</c>; numeric values
/// are load-bearing on disk — renaming an enum member is safe but
/// reordering would silently corrupt every row.
/// </summary>
/// <remarks>
/// The ordinal also doubles as the canonical depth: <see cref="Org"/>
/// at depth 0, ..., <see cref="Run"/> at depth 5. The service layer
/// (P4.2) enforces the <c>Type == Depth</c> invariant on insert; this
/// story only stores the value.
/// </remarks>
public enum ScopeType
{
    Org = 0,
    Tenant = 1,
    Team = 2,
    Repo = 3,
    Template = 4,
    Run = 5,
}
