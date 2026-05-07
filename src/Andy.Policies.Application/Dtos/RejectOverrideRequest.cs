// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Request payload for <c>IOverrideService.RejectAsync</c> (P9 follow-up
/// #201, 2026-05-07). The reason is required and persisted to
/// <c>Override.RevocationReason</c> — the column is reused so the audit
/// chain can record the human-readable cause regardless of which
/// terminal state was reached. The <see cref="RejectionReason"/>
/// alias makes the intent explicit on the wire.
/// </summary>
public sealed record RejectOverrideRequest(string RejectionReason);
