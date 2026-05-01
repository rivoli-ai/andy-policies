// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Shared.Auditing;

/// <summary>
/// Marks a DTO property as sensitive — the
/// <c>JsonPatchDiffGenerator</c> emits the literal string
/// <c>"***"</c> in place of the actual value for any
/// <c>add</c> / <c>replace</c> op (P6.3, story
/// rivoli-ai/andy-policies#43). <c>remove</c> ops have no
/// <c>value</c> and so are unaffected.
/// </summary>
/// <remarks>
/// Unlike <see cref="AuditIgnoreAttribute"/>, the <i>fact</i>
/// that the property changed is still recorded — only the
/// concrete value is redacted. This keeps the chain
/// tamper-evident (the diff still hashes; the audit chain
/// still records "this property mutated") while preventing
/// secrets / PII from entering the hashed envelope.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class AuditRedactAttribute : Attribute
{
}
