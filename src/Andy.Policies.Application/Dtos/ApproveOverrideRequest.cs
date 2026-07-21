// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>Audit rationale supplied by the override approver.</summary>
public sealed record ApproveOverrideRequest(string? Rationale);
