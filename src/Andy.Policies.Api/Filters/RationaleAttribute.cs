// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Api.Filters;

/// <summary>
/// Marks the rationale property on a request DTO so
/// <see cref="RationaleRequiredFilter"/> can find it without
/// relying solely on the property name (P6.4, story
/// rivoli-ai/andy-policies#44). Most DTOs already use the
/// canonical name <c>Rationale</c>; this attribute exists for
/// the cases where the field is named differently
/// (<c>Reason</c>, <c>Note</c>, etc.).
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class RationaleAttribute : Attribute
{
}
