// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>Audit rationale for creating a new Draft from a source version.</summary>
public sealed record BumpPolicyVersionRequest(string? Rationale);
