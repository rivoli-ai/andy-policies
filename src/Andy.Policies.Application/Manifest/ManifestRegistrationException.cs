// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Manifest;

/// <summary>
/// Thrown by an <c>I*ManifestClient</c> when a consumer rejects (or
/// fails to acknowledge) a manifest POST. Surfaces the block name
/// (auth / rbac / settings) so the hosted service can log + crash
/// loudly per P10.3 fail-loud semantics.
/// </summary>
public sealed class ManifestRegistrationException : Exception
{
    public string Block { get; }

    public ManifestRegistrationException(string block, string message)
        : base(message)
    {
        Block = block;
    }

    public ManifestRegistrationException(string block, string message, Exception inner)
        : base(message, inner)
    {
        Block = block;
    }
}
