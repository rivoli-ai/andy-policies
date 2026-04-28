// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Enums;

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Request payload for <c>IBindingService.CreateAsync</c> (P3.2, story
/// rivoli-ai/andy-policies#20). The target version must exist and not be in
/// <c>LifecycleState.Retired</c>; the service throws
/// <c>BindingRetiredVersionException</c> on a retired target.
/// </summary>
public sealed record CreateBindingRequest(
    Guid PolicyVersionId,
    BindingTargetType TargetType,
    string TargetRef,
    BindStrength BindStrength);
