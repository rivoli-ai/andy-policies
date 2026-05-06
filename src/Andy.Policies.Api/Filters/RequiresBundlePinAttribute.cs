// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Api.Filters;

/// <summary>
/// Marker attribute applied to read endpoints that must enforce the
/// bundle-pinning gate from P8.4 (rivoli-ai/andy-policies#84). Paired
/// with <see cref="BundlePinningFilter"/> which inspects this metadata
/// per-action — the gate is positive-annotation rather than blanket
/// middleware so a missing entry can never cause a service-wide
/// regression.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequiresBundlePinAttribute : Attribute
{
}
