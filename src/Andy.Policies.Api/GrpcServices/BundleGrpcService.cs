// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace Andy.Policies.Api.GrpcServices;

/// <summary>
/// gRPC surface for bundle pinning (P8.6, story
/// rivoli-ai/andy-policies#86). Six RPCs delegate to the same
/// <see cref="IBundleService"/> + <see cref="IBundleResolver"/> +
/// <see cref="IBundleDiffService"/> powering REST (P8.3) and MCP
/// (P8.5) — zero duplicated business logic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Status mapping.</b> <see cref="ValidationException"/> →
/// <see cref="StatusCode.InvalidArgument"/>;
/// <see cref="ConflictException"/> → <see cref="StatusCode.AlreadyExists"/>;
/// missing subject → <see cref="StatusCode.Unauthenticated"/>;
/// unknown id → <see cref="StatusCode.NotFound"/>; unmapped
/// internal failures bubble as <see cref="StatusCode.Internal"/>
/// via the framework.
/// </para>
/// </remarks>
[Authorize]
public class BundleGrpcService : Andy.Policies.Api.Protos.BundleService.BundleServiceBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly IBundleService _bundles;
    private readonly IBundleResolver _resolver;
    private readonly IBundleDiffService _diff;

    public BundleGrpcService(
        IBundleService bundles,
        IBundleResolver resolver,
        IBundleDiffService diff)
    {
        _bundles = bundles;
        _resolver = resolver;
        _diff = diff;
    }

    public override async Task<BundleMessage> CreateBundle(
        Andy.Policies.Api.Protos.CreateBundleRequest request, ServerCallContext context)
    {
        var actor = ResolveActor(context);
        try
        {
            var dto = await _bundles.CreateAsync(
                new Application.Interfaces.CreateBundleRequest(
                    request.Name,
                    request.HasDescription ? request.Description : null,
                    request.Rationale),
                actor,
                context.CancellationToken).ConfigureAwait(false);
            return ToMessage(dto);
        }
        catch (ValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (ConflictException ex)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, ex.Message));
        }
    }

    public override async Task<ListBundlesResponse> ListBundles(
        ListBundlesRequest request, ServerCallContext context)
    {
        var skip = Math.Max(0, request.Skip);
        var take = request.Take <= 0
            ? DefaultPageSize
            : Math.Clamp(request.Take, 1, MaxPageSize);
        var rows = await _bundles.ListAsync(
            new ListBundlesFilter(request.IncludeDeleted, skip, take),
            context.CancellationToken).ConfigureAwait(false);
        var resp = new ListBundlesResponse();
        foreach (var dto in rows) resp.Bundles.Add(ToMessage(dto));
        return resp;
    }

    public override async Task<BundleMessage> GetBundle(
        GetBundleRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var bid))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.Id}' is not a valid GUID."));
        }
        var dto = await _bundles.GetAsync(bid, context.CancellationToken).ConfigureAwait(false);
        if (dto is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Bundle {bid} not found."));
        }
        return ToMessage(dto);
    }

    public override async Task<ResolveBundleResponse> ResolveBundle(
        ResolveBundleRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var bid))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.Id}' is not a valid GUID."));
        }
        if (!Enum.TryParse<BindingTargetType>(request.TargetType, ignoreCase: true, out var tt))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"target_type '{request.TargetType}' is not a valid BindingTargetType."));
        }
        if (string.IsNullOrWhiteSpace(request.TargetRef))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "target_ref is required."));
        }

        var result = await _resolver.ResolveAsync(bid, tt, request.TargetRef, context.CancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound,
                $"Bundle {bid} does not exist or is soft-deleted."));
        }

        var resp = new ResolveBundleResponse
        {
            BundleId = result.BundleId.ToString(),
            BundleName = result.BundleName,
            SnapshotHash = result.SnapshotHash,
            CapturedAt = result.CapturedAt.ToString("o"),
            TargetType = result.TargetType.ToString(),
            TargetRef = result.TargetRef,
            Count = result.Count,
        };
        foreach (var b in result.Bindings)
        {
            var msg = new ResolvedBundleBindingMessage
            {
                BindingId = b.BindingId.ToString(),
                PolicyId = b.PolicyId.ToString(),
                PolicyName = b.PolicyName,
                PolicyVersionId = b.PolicyVersionId.ToString(),
                VersionNumber = b.VersionNumber,
                Enforcement = b.Enforcement,
                Severity = b.Severity,
                BindStrength = b.BindStrength.ToString(),
            };
            foreach (var s in b.Scopes) msg.Scopes.Add(s);
            resp.Bindings.Add(msg);
        }
        return resp;
    }

    public override async Task<BundleMessage> DeleteBundle(
        DeleteBundleRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var bid))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.Id}' is not a valid GUID."));
        }
        var actor = ResolveActor(context);
        try
        {
            var deleted = await _bundles.SoftDeleteAsync(bid, actor, request.Rationale, context.CancellationToken)
                .ConfigureAwait(false);
            if (!deleted)
            {
                throw new RpcException(new Status(StatusCode.NotFound,
                    $"Bundle {bid} does not exist or is already soft-deleted."));
            }
            var dto = await _bundles.GetAsync(bid, context.CancellationToken).ConfigureAwait(false);
            return ToMessage(dto!);
        }
        catch (ValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<DiffBundlesResponse> DiffBundles(
        DiffBundlesRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.FromId, out var fromId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"from_id '{request.FromId}' is not a valid GUID."));
        }
        if (!Guid.TryParse(request.ToId, out var toId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"to_id '{request.ToId}' is not a valid GUID."));
        }

        var result = await _diff.DiffAsync(fromId, toId, context.CancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound,
                "One or both bundles not found or soft-deleted."));
        }

        return new DiffBundlesResponse
        {
            FromId = result.FromId.ToString(),
            FromSnapshotHash = result.FromSnapshotHash,
            ToId = result.ToId.ToString(),
            ToSnapshotHash = result.ToSnapshotHash,
            Rfc6902PatchJson = result.Rfc6902PatchJson,
            OpCount = result.OpCount,
        };
    }

    private static string ResolveActor(ServerCallContext context)
    {
        var user = context.GetHttpContext().User;
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name;
        if (string.IsNullOrEmpty(sub))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "no subject claim"));
        }
        return sub;
    }

    private static BundleMessage ToMessage(BundleDto dto)
    {
        var msg = new BundleMessage
        {
            Id = dto.Id.ToString(),
            Name = dto.Name,
            CreatedAt = dto.CreatedAt.ToString("o"),
            CreatedBySubjectId = dto.CreatedBySubjectId,
            SnapshotHash = dto.SnapshotHash,
            State = dto.State,
        };
        if (dto.Description is not null) msg.Description = dto.Description;
        if (dto.DeletedAt is { } da) msg.DeletedAt = da.ToString("o");
        if (dto.DeletedBySubjectId is not null) msg.DeletedBySubjectId = dto.DeletedBySubjectId;
        return msg;
    }
}
