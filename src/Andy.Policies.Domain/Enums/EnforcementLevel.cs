// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Enums;

/// <summary>
/// RFC 2119 posture for a <see cref="Entities.PolicyVersion"/>.
/// See https://www.rfc-editor.org/rfc/rfc2119.
/// </summary>
/// <remarks>
/// This service only stores the level — it does NOT enforce. Consumers
/// (Conductor ActionBus admission, andy-tasks Epic AC gates) translate:
/// <list type="bullet">
///   <item><term>Must</term><description>hard deny on non-compliance</description></item>
///   <item><term>Should</term><description>warn + proceed; require a rationale if overridden</description></item>
///   <item><term>May</term><description>informational; no gate</description></item>
/// </list>
/// Per ADR 0001 §6 the wire format is uppercase (`MUST` / `SHOULD` / `MAY`)
/// to preserve the RFC 2119 convention that consumers can grep for directly.
/// The JSON serializer configuration lives in <c>Program.cs</c> (P1.5).
/// </remarks>
public enum EnforcementLevel
{
    /// <summary>Optional / discretionary. Consumers treat as an informational hint.</summary>
    May = 0,

    /// <summary>Recommended; non-compliance must be justified with a rationale.</summary>
    Should = 1,

    /// <summary>Required; non-compliance is a violation that blocks the consuming action.</summary>
    Must = 2,
}
