// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace Andy.Policies.Api.GrpcServices;

/// <summary>
/// gRPC surface for the binding catalog (P3.6, story
/// rivoli-ai/andy-policies#24). Six RPCs delegate to the same
/// <see cref="IBindingService"/> + <see cref="IBindingResolver"/> as REST
/// (P3.3, P3.4), MCP (P3.5), and CLI (P3.7). Service exceptions translate
/// to gRPC status codes per the table below; the equivalent HTTP
/// mappings live in <c>PolicyExceptionHandler</c>:
///
/// <list type="table">
///   <listheader><term>Exception</term><description>gRPC status / HTTP equivalent</description></listheader>
///   <item><term><see cref="BindingRetiredVersionException"/></term><description>FailedPrecondition / 409</description></item>
///   <item><term><see cref="ConflictException"/></term><description>AlreadyExists / 409</description></item>
///   <item><term><see cref="ValidationException"/></term><description>InvalidArgument / 400</description></item>
///   <item><term><see cref="NotFoundException"/></term><description>NotFound / 404</description></item>
/// </list>
/// </summary>
[Authorize]
public class BindingsGrpcService : Andy.Policies.Api.Protos.BindingService.BindingServiceBase
{
    private readonly IBindingService _bindings;
    private readonly IBindingResolver _resolver;

    public BindingsGrpcService(IBindingService bindings, IBindingResolver resolver)
    {
        _bindings = bindings;
        _resolver = resolver;
    }

    public override async Task<BindingResponse> CreateBinding(
        Andy.Policies.Api.Protos.CreateBindingRequest request, ServerCallContext context)
    {
        var versionId = ParseGuidOrThrow(request.PolicyVersionId, "policy_version_id");
        var targetType = ToDomainTargetType(request.TargetType);
        var bindStrength = ToDomainBindStrength(request.BindStrength);

        try
        {
            var dto = await _bindings.CreateAsync(
                new Andy.Policies.Application.Dtos.CreateBindingRequest(
                    versionId, targetType, request.TargetRef, bindStrength),
                ResolveSubjectId(context),
                context.CancellationToken);
            return new BindingResponse { Binding = ToMessage(dto) };
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override async Task<DeleteBindingResponse> DeleteBinding(
        DeleteBindingRequest request, ServerCallContext context)
    {
        var id = ParseGuidOrThrow(request.Id, "id");
        try
        {
            await _bindings.DeleteAsync(
                id,
                ResolveSubjectId(context),
                string.IsNullOrEmpty(request.Rationale) ? null : request.Rationale,
                context.CancellationToken);
            return new DeleteBindingResponse();
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override async Task<BindingResponse> GetBinding(
        GetBindingRequest request, ServerCallContext context)
    {
        var id = ParseGuidOrThrow(request.Id, "id");
        var dto = await _bindings.GetAsync(id, context.CancellationToken);
        if (dto is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Binding {id} not found."));
        }
        return new BindingResponse { Binding = ToMessage(dto) };
    }

    public override async Task<ListBindingsResponse> ListBindingsByPolicyVersion(
        ListBindingsByPolicyVersionRequest request, ServerCallContext context)
    {
        var versionId = ParseGuidOrThrow(request.PolicyVersionId, "policy_version_id");
        var rows = await _bindings.ListByPolicyVersionAsync(
            versionId, request.IncludeDeleted, context.CancellationToken);
        var response = new ListBindingsResponse();
        response.Bindings.AddRange(rows.Select(ToMessage));
        return response;
    }

    public override async Task<ListBindingsResponse> ListBindingsByTarget(
        ListBindingsByTargetRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.TargetRef))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "target_ref is required."));
        }
        var targetType = ToDomainTargetType(request.TargetType);
        var rows = await _bindings.ListByTargetAsync(targetType, request.TargetRef, context.CancellationToken);
        var response = new ListBindingsResponse();
        response.Bindings.AddRange(rows.Select(ToMessage));
        return response;
    }

    public override async Task<Andy.Policies.Api.Protos.ResolveBindingsResponse> ResolveBindings(
        ResolveBindingsRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.TargetRef))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "target_ref is required."));
        }
        var targetType = ToDomainTargetType(request.TargetType);
        var dto = await _resolver.ResolveExactAsync(
            targetType, request.TargetRef, context.CancellationToken);

        var response = new Andy.Policies.Api.Protos.ResolveBindingsResponse
        {
            TargetType = ToProtoTargetType(dto.TargetType),
            TargetRef = dto.TargetRef,
            Count = dto.Count,
        };
        response.Bindings.AddRange(dto.Bindings.Select(ToMessage));
        return response;
    }

    // -- helpers --------------------------------------------------------------

    private static Guid ParseGuidOrThrow(string raw, string field)
    {
        if (!Guid.TryParse(raw, out var guid))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{field} '{raw}' is not a valid GUID."));
        }
        return guid;
    }

    private static string ResolveSubjectId(ServerCallContext context)
    {
        // Mirrors the REST controller's actor-fallback firewall (#13): refuse
        // to act when no subject id is on the principal rather than write a
        // fallback string into the catalog.
        var http = context.GetHttpContext();
        var sub = http?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? http?.User.Identity?.Name;
        if (string.IsNullOrEmpty(sub))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                "Authentication required: no subject id present on the caller's claims principal."));
        }
        return sub;
    }

    private static BindingTargetType ToDomainTargetType(TargetType wire) => wire switch
    {
        TargetType.Template => BindingTargetType.Template,
        TargetType.Repo => BindingTargetType.Repo,
        TargetType.ScopeNode => BindingTargetType.ScopeNode,
        TargetType.Tenant => BindingTargetType.Tenant,
        TargetType.Org => BindingTargetType.Org,
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
            "target_type is required (TARGET_TYPE_UNSPECIFIED is not a valid value).")),
    };

    private static TargetType ToProtoTargetType(BindingTargetType domain) => domain switch
    {
        BindingTargetType.Template => TargetType.Template,
        BindingTargetType.Repo => TargetType.Repo,
        BindingTargetType.ScopeNode => TargetType.ScopeNode,
        BindingTargetType.Tenant => TargetType.Tenant,
        BindingTargetType.Org => TargetType.Org,
        _ => throw new InvalidOperationException($"Unknown BindingTargetType: {domain}"),
    };

    private static Domain.Enums.BindStrength ToDomainBindStrength(Andy.Policies.Api.Protos.BindStrength wire) => wire switch
    {
        Andy.Policies.Api.Protos.BindStrength.Mandatory => Domain.Enums.BindStrength.Mandatory,
        Andy.Policies.Api.Protos.BindStrength.Recommended => Domain.Enums.BindStrength.Recommended,
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
            "bind_strength is required (BIND_STRENGTH_UNSPECIFIED is not a valid value).")),
    };

    private static Andy.Policies.Api.Protos.BindStrength ToProtoBindStrength(Domain.Enums.BindStrength domain) => domain switch
    {
        Domain.Enums.BindStrength.Mandatory => Andy.Policies.Api.Protos.BindStrength.Mandatory,
        Domain.Enums.BindStrength.Recommended => Andy.Policies.Api.Protos.BindStrength.Recommended,
        _ => throw new InvalidOperationException($"Unknown BindStrength: {domain}"),
    };

    private static RpcException MapToRpcException(Exception ex) => ex switch
    {
        // BindingRetiredVersionException is a ConflictException — match the more
        // specific type first so FailedPrecondition wins over AlreadyExists.
        BindingRetiredVersionException r => new RpcException(new Status(StatusCode.FailedPrecondition, r.Message)),
        ValidationException v => new RpcException(new Status(StatusCode.InvalidArgument, v.Message)),
        NotFoundException n => new RpcException(new Status(StatusCode.NotFound, n.Message)),
        ConflictException c => new RpcException(new Status(StatusCode.AlreadyExists, c.Message)),
        RpcException existing => existing,
        _ => throw new InvalidOperationException(
            $"Unmapped exception in BindingsGrpcService: {ex.GetType().Name}", ex),
    };

    private static BindingMessage ToMessage(BindingDto dto) => new()
    {
        Id = dto.Id.ToString(),
        PolicyVersionId = dto.PolicyVersionId.ToString(),
        TargetType = ToProtoTargetType(dto.TargetType),
        TargetRef = dto.TargetRef,
        BindStrength = ToProtoBindStrength(dto.BindStrength),
        CreatedAt = dto.CreatedAt.ToString("o"),
        CreatedBySubjectId = dto.CreatedBySubjectId,
        DeletedAt = dto.DeletedAt?.ToString("o") ?? string.Empty,
        DeletedBySubjectId = dto.DeletedBySubjectId ?? string.Empty,
    };

    private static ResolvedBindingMessage ToMessage(ResolvedBindingDto dto)
    {
        var msg = new ResolvedBindingMessage
        {
            BindingId = dto.BindingId.ToString(),
            PolicyId = dto.PolicyId.ToString(),
            PolicyName = dto.PolicyName,
            PolicyVersionId = dto.PolicyVersionId.ToString(),
            VersionNumber = dto.VersionNumber,
            VersionState = dto.VersionState,
            Enforcement = dto.Enforcement,
            Severity = dto.Severity,
            BindStrength = ToProtoBindStrength(dto.BindStrength),
        };
        msg.Scopes.AddRange(dto.Scopes);
        return msg;
    }
}
