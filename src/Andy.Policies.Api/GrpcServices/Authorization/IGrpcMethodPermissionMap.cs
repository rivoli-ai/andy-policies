// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Api.GrpcServices.Authorization;

/// <summary>
/// Maps a fully-qualified gRPC method name (e.g.
/// <c>"/andy.policies.v1.LifecycleService/PublishVersion"</c>) to a
/// permission code from the P7.1 manifest. The
/// <see cref="RbacServerInterceptor"/> consults this map for every
/// inbound RPC; an unmapped method is a hard failure (10/Internal)
/// rather than fail-open.
/// </summary>
public interface IGrpcMethodPermissionMap
{
    bool TryGetPermission(string fullyQualifiedMethod, out string permissionCode);

    IReadOnlyDictionary<string, string> Entries { get; }
}
