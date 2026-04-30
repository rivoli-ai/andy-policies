// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Domain.Enums;

/// <summary>
/// What an <see cref="Entities.Override"/> does to the policy that
/// would otherwise apply (P5.1, story rivoli-ai/andy-policies#49):
/// <list type="bullet">
///   <item><see cref="Exempt"/> — temporarily skip the bound policy
///     for the principal/cohort. Carries no replacement (CHECK
///     constraint enforces null <c>ReplacementPolicyVersionId</c>).
///   </item>
///   <item><see cref="Replace"/> — substitute a different
///     <see cref="PolicyVersion"/> while the override is active.
///     Carries a non-null replacement (CHECK constraint enforces
///     non-null <c>ReplacementPolicyVersionId</c>).
///   </item>
/// </list>
/// </summary>
public enum OverrideEffect
{
    Exempt = 0,
    Replace = 1,
}
