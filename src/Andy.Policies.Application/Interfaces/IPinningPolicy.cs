// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Reads <c>andy.policies.bundleVersionPinning</c> from andy-settings
/// (P8.4, story rivoli-ai/andy-policies#84). When <c>true</c> (the
/// shipped default per <c>config/registration.json</c>) the gated
/// read endpoints (<c>GET /api/policies*</c>,
/// <c>GET /api/bindings/resolve</c>,
/// <c>GET /api/scopes/{id}/effective-policies</c>) reject requests
/// that omit <c>?bundleId=</c> with a 400 Problem Details response.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the <see cref="IRationalePolicy"/> + experimental-overrides
/// gate shape from P5.4 — sync read off the live
/// <c>ISettingsSnapshot</c>, fail-safe to the manifest default if the
/// snapshot has not yet observed the key. Fail-closed (i.e. require
/// pinning when in doubt) is the safer posture: returning 400 is
/// never a correctness regression, while silently serving live state
/// under a transient settings outage would be.
/// </para>
/// </remarks>
public interface IPinningPolicy
{
    /// <summary>
    /// <c>true</c> when callers must supply <c>?bundleId=</c> on
    /// pinning-gated reads.
    /// </summary>
    bool IsPinningRequired { get; }
}
