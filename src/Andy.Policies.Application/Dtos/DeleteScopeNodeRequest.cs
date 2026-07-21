// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>Audit rationale for deleting a leaf scope node.</summary>
public sealed record DeleteScopeNodeRequest(string? Rationale);
