// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using ProtoScopeType = Andy.Policies.Api.Protos.ScopeType;
using DomainScopeType = Andy.Policies.Domain.Enums.ScopeType;
using ProtoBindStrengthMessage = Andy.Policies.Api.Protos.ProtoBindStrength;

namespace Andy.Policies.Api.GrpcServices;

/// <summary>
/// gRPC surface for the scope hierarchy (P4.6, story
/// rivoli-ai/andy-policies#34). Six RPCs delegate to the same
/// <see cref="IScopeService"/> + <see cref="IBindingResolutionService"/>
/// powering REST (P4.5), MCP, and CLI. Service exceptions translate
/// to gRPC status codes per the table below; the equivalent HTTP
/// mappings live in <c>PolicyExceptionHandler</c>:
///
/// <list type="table">
///   <listheader><term>Exception</term><description>gRPC status / HTTP equivalent</description></listheader>
///   <item><term><see cref="InvalidScopeTypeException"/></term><description>FailedPrecondition / 400 (scope.parent-type-mismatch)</description></item>
///   <item><see cref="ScopeRefConflictException"/><description>AlreadyExists / 409 (scope.ref-conflict)</description></item>
///   <item><term><see cref="ScopeHasDescendantsException"/></term><description>FailedPrecondition / 409 (scope.has-descendants)</description></item>
///   <item><term><see cref="ValidationException"/></term><description>InvalidArgument / 400</description></item>
///   <item><term><see cref="NotFoundException"/></term><description>NotFound / 404</description></item>
/// </list>
/// </summary>
[Authorize]
public class ScopesGrpcService : Andy.Policies.Api.Protos.ScopesService.ScopesServiceBase
{
    private readonly IScopeService _scopes;
    private readonly IBindingResolutionService _resolver;

    public ScopesGrpcService(IScopeService scopes, IBindingResolutionService resolver)
    {
        _scopes = scopes;
        _resolver = resolver;
    }

    public override async Task<ListScopesResponse> ListScopes(
        ListScopesRequest request, ServerCallContext context)
    {
        DomainScopeType? filter = request.Type == ProtoScopeType.Unspecified
            ? null
            : ToDomainScopeType(request.Type);
        var rows = await _scopes.ListAsync(filter, context.CancellationToken)
            .ConfigureAwait(false);
        var response = new ListScopesResponse();
        response.Nodes.AddRange(rows.Select(ToMessage));
        return response;
    }

    public override async Task<ScopeNodeResponse> GetScope(
        GetScopeRequest request, ServerCallContext context)
    {
        var id = ParseGuidOrThrow(request.Id, "id");
        var dto = await _scopes.GetAsync(id, context.CancellationToken).ConfigureAwait(false);
        if (dto is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"ScopeNode {id} not found."));
        }
        return new ScopeNodeResponse { Node = ToMessage(dto) };
    }

    public override async Task<GetScopeTreeResponse> GetScopeTree(
        GetScopeTreeRequest request, ServerCallContext context)
    {
        var forest = await _scopes.GetTreeAsync(context.CancellationToken).ConfigureAwait(false);
        var response = new GetScopeTreeResponse();
        response.Forest.AddRange(forest.Select(ToMessage));
        return response;
    }

    public override async Task<ScopeNodeResponse> CreateScope(
        CreateScopeRequest request, ServerCallContext context)
    {
        Guid? parentId = null;
        if (!string.IsNullOrEmpty(request.ParentId))
        {
            parentId = ParseGuidOrThrow(request.ParentId, "parent_id");
        }
        var scopeType = ToDomainScopeType(request.Type);

        try
        {
            var dto = await _scopes.CreateAsync(
                new CreateScopeNodeRequest(parentId, scopeType, request.TargetRef, request.DisplayName),
                context.CancellationToken).ConfigureAwait(false);
            return new ScopeNodeResponse { Node = ToMessage(dto) };
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override async Task<DeleteScopeResponse> DeleteScope(
        DeleteScopeRequest request, ServerCallContext context)
    {
        var id = ParseGuidOrThrow(request.Id, "id");
        try
        {
            await _scopes.DeleteAsync(id, context.CancellationToken).ConfigureAwait(false);
            return new DeleteScopeResponse();
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override async Task<EffectivePolicySetResponse> GetEffectivePolicies(
        GetEffectivePoliciesRequest request, ServerCallContext context)
    {
        var id = ParseGuidOrThrow(request.Id, "id");
        try
        {
            var result = await _resolver.ResolveForScopeAsync(id, context.CancellationToken)
                .ConfigureAwait(false);
            var response = new EffectivePolicySetResponse
            {
                ScopeNodeId = result.ScopeNodeId?.ToString() ?? string.Empty,
            };
            response.Policies.AddRange(result.Policies.Select(ToMessage));
            return response;
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
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

    private static DomainScopeType ToDomainScopeType(ProtoScopeType wire) => wire switch
    {
        ProtoScopeType.Org => DomainScopeType.Org,
        ProtoScopeType.Tenant => DomainScopeType.Tenant,
        ProtoScopeType.Team => DomainScopeType.Team,
        ProtoScopeType.Repo => DomainScopeType.Repo,
        ProtoScopeType.Template => DomainScopeType.Template,
        ProtoScopeType.Run => DomainScopeType.Run,
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
            "type is required (SCOPE_TYPE_UNSPECIFIED is not a valid value).")),
    };

    private static ProtoScopeType ToProtoScopeType(DomainScopeType domain) => domain switch
    {
        DomainScopeType.Org => ProtoScopeType.Org,
        DomainScopeType.Tenant => ProtoScopeType.Tenant,
        DomainScopeType.Team => ProtoScopeType.Team,
        DomainScopeType.Repo => ProtoScopeType.Repo,
        DomainScopeType.Template => ProtoScopeType.Template,
        DomainScopeType.Run => ProtoScopeType.Run,
        _ => throw new InvalidOperationException($"Unknown ScopeType: {domain}"),
    };

    private static ProtoBindStrengthMessage ToProtoBindStrength(Andy.Policies.Domain.Enums.BindStrength domain) => domain switch
    {
        Andy.Policies.Domain.Enums.BindStrength.Mandatory => ProtoBindStrengthMessage.BindStrengthProtoMandatory,
        Andy.Policies.Domain.Enums.BindStrength.Recommended => ProtoBindStrengthMessage.BindStrengthProtoRecommended,
        _ => throw new InvalidOperationException($"Unknown BindStrength: {domain}"),
    };

    private static RpcException MapToRpcException(Exception ex) => ex switch
    {
        // Order matters: the more specific scope exceptions inherit
        // from ConflictException / ValidationException, so they must
        // be caught first.
        InvalidScopeTypeException ist =>
            new RpcException(new Status(StatusCode.FailedPrecondition, ist.Message)),
        ScopeRefConflictException src =>
            new RpcException(new Status(StatusCode.AlreadyExists, src.Message)),
        ScopeHasDescendantsException shd =>
            new RpcException(new Status(StatusCode.FailedPrecondition, shd.Message)),
        ValidationException v =>
            new RpcException(new Status(StatusCode.InvalidArgument, v.Message)),
        NotFoundException n =>
            new RpcException(new Status(StatusCode.NotFound, n.Message)),
        ConflictException c =>
            new RpcException(new Status(StatusCode.AlreadyExists, c.Message)),
        RpcException existing => existing,
        _ => throw new InvalidOperationException(
            $"Unmapped exception in ScopesGrpcService: {ex.GetType().Name}", ex),
    };

    private static ScopeNodeMessage ToMessage(ScopeNodeDto dto) => new()
    {
        Id = dto.Id.ToString(),
        ParentId = dto.ParentId?.ToString() ?? string.Empty,
        Type = ToProtoScopeType(dto.Type),
        TargetRef = dto.Ref,
        DisplayName = dto.DisplayName,
        MaterializedPath = dto.MaterializedPath,
        Depth = dto.Depth,
        CreatedAt = dto.CreatedAt.ToString("o"),
        UpdatedAt = dto.UpdatedAt.ToString("o"),
    };

    private static ScopeTreeMessage ToMessage(ScopeTreeDto dto)
    {
        var msg = new ScopeTreeMessage { Node = ToMessage(dto.Node) };
        msg.Children.AddRange(dto.Children.Select(ToMessage));
        return msg;
    }

    private static EffectivePolicyMessage ToMessage(EffectivePolicyDto dto) => new()
    {
        PolicyId = dto.PolicyId.ToString(),
        PolicyVersionId = dto.PolicyVersionId.ToString(),
        PolicyKey = dto.PolicyKey,
        Version = dto.Version,
        BindStrength = ToProtoBindStrength(dto.BindStrength),
        SourceBindingId = dto.SourceBindingId.ToString(),
        SourceScopeNodeId = dto.SourceScopeNodeId?.ToString() ?? string.Empty,
        SourceScopeType = dto.SourceScopeType is { } st
            ? ToProtoScopeType(st)
            : ProtoScopeType.Unspecified,
        SourceDepth = dto.SourceDepth,
    };
}
