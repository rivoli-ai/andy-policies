// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Application.Interfaces;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Api.GrpcServices.Authorization;

/// <summary>
/// Global gRPC server interceptor that applies the same RBAC contract
/// as the REST <see cref="Andy.Policies.Api.Authorization.RbacAuthorizationHandler"/>:
/// extract subject + groups from the inbound principal, look up the
/// permission code for the called method, and delegate to
/// <see cref="IRbacChecker"/>. Deny → <c>RpcException(PermissionDenied)</c>.
/// P7.6 (#64).
/// </summary>
/// <remarks>
/// <para>
/// <b>Unmapped methods on enforced services</b> throw
/// <c>RpcException(Internal, "no permission mapping")</c> rather than
/// allowing through. This is fail-closed by design — adding a new RPC
/// without a permission code in
/// <see cref="GrpcMethodPermissionMap"/> would be a security gap.
/// </para>
/// <para>
/// <b>Items service is intentionally bypassed</b> — see
/// <see cref="GrpcMethodPermissionMap.IsEnforcedService"/>.
/// </para>
/// </remarks>
public sealed class RbacServerInterceptor : Interceptor
{
    private readonly IRbacChecker _rbac;
    private readonly IGrpcMethodPermissionMap _map;
    private readonly ILogger<RbacServerInterceptor> _log;

    public RbacServerInterceptor(
        IRbacChecker rbac,
        IGrpcMethodPermissionMap map,
        ILogger<RbacServerInterceptor> log)
    {
        _rbac = rbac;
        _map = map;
        _log = log;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await EnforceAsync(context).ConfigureAwait(false);
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await EnforceAsync(context).ConfigureAwait(false);
        await continuation(request, responseStream, context).ConfigureAwait(false);
    }

    private async Task EnforceAsync(ServerCallContext context)
    {
        if (!GrpcMethodPermissionMap.IsEnforcedService(context.Method))
        {
            return;
        }
        if (!_map.TryGetPermission(context.Method, out var permissionCode))
        {
            // Hard fail rather than allow-through: a missing entry is
            // a security gap, not an oversight to silently paper over.
            throw new RpcException(new Status(StatusCode.Internal,
                $"no permission mapping for gRPC method {context.Method}"));
        }

        var user = context.GetHttpContext().User;
        var subjectId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? user.FindFirstValue("sub")
                     ?? user.Identity?.Name
                     ?? string.Empty;
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "no subject claim"));
        }
        var groups = user.FindAll("groups").Select(c => c.Value).ToList();

        var decision = await _rbac.CheckAsync(
            subjectId, permissionCode, groups,
            resourceInstanceId: null, // P7.6 v1: no per-instance for gRPC.
            ct: context.CancellationToken).ConfigureAwait(false);

        if (!decision.Allowed)
        {
            _log.LogInformation(
                "grpc rbac deny subject={Sub} method={Method} reason={Reason}",
                subjectId, context.Method, decision.Reason);
            throw new RpcException(new Status(StatusCode.PermissionDenied, decision.Reason));
        }
    }
}
