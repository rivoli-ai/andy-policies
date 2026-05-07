// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Enums;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Api.GrpcServices;

/// <summary>
/// gRPC surface for lifecycle transitions on a <c>PolicyVersion</c> (P2.6,
/// story rivoli-ai/andy-policies#16). Delegates to
/// <see cref="ILifecycleTransitionService"/> — the same service powering REST
/// (P2.3), MCP (P2.5), and CLI (P2.7). Service exceptions translate to gRPC
/// status codes per the table below; the equivalent HTTP mappings live in
/// <c>PolicyExceptionHandler</c>:
///
/// <list type="table">
///   <listheader><term>Exception</term><description>gRPC status / HTTP equivalent</description></listheader>
///   <item><term><see cref="RationaleRequiredException"/></term><description>InvalidArgument / 400</description></item>
///   <item><term><see cref="ValidationException"/></term><description>InvalidArgument / 400</description></item>
///   <item><term><see cref="NotFoundException"/></term><description>NotFound / 404</description></item>
///   <item><term><see cref="InvalidLifecycleTransitionException"/></term><description>FailedPrecondition / 409</description></item>
///   <item><term><see cref="ConcurrentPublishException"/></term><description>Aborted / 409</description></item>
///   <item><term><see cref="ConflictException"/></term><description>AlreadyExists / 409</description></item>
///   <item><term><see cref="DbUpdateConcurrencyException"/></term><description>Aborted / 412</description></item>
/// </list>
/// </summary>
[Authorize]
public class LifecycleGrpcService : Protos.LifecycleService.LifecycleServiceBase
{
    private readonly ILifecycleTransitionService _transitions;

    public LifecycleGrpcService(ILifecycleTransitionService transitions)
    {
        _transitions = transitions;
    }

    public override async Task<PolicyVersionResponse> PublishVersion(
        PublishVersionRequest request, ServerCallContext context)
    {
        var policyId = ParseGuidOrThrow(request.PolicyId, "policy_id");
        var versionId = ParseGuidOrThrow(request.VersionId, "version_id");
        try
        {
            var dto = await _transitions.TransitionAsync(
                policyId, versionId, LifecycleState.Active,
                request.Rationale ?? string.Empty,
                ResolveSubjectId(context),
                ct: context.CancellationToken);
            return new PolicyVersionResponse { Version = ToMessage(dto) };
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override async Task<PolicyVersionResponse> TransitionVersion(
        TransitionVersionRequest request, ServerCallContext context)
    {
        var policyId = ParseGuidOrThrow(request.PolicyId, "policy_id");
        var versionId = ParseGuidOrThrow(request.VersionId, "version_id");
        if (!Enum.TryParse<LifecycleState>(request.TargetState, ignoreCase: true, out var target)
            || target == LifecycleState.Draft)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"target_state '{request.TargetState}' is not valid. Use Active, WindingDown, or Retired."));
        }

        try
        {
            var dto = await _transitions.TransitionAsync(
                policyId, versionId, target,
                request.Rationale ?? string.Empty,
                ResolveSubjectId(context),
                ct: context.CancellationToken);
            return new PolicyVersionResponse { Version = ToMessage(dto) };
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override Task<MatrixResponse> GetMatrix(GetMatrixRequest request, ServerCallContext context)
    {
        var rules = _transitions.GetMatrix();
        var response = new MatrixResponse();
        foreach (var rule in rules)
        {
            response.Rules.Add(new MatrixRule
            {
                From = rule.From.ToString(),
                To = rule.To.ToString(),
                Name = rule.Name,
            });
        }
        return Task.FromResult(response);
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
        // Mirrors the REST controller's actor-fallback firewall (#13): refuse to
        // act when no subject id is on the principal rather than write a fallback
        // string into the catalog. The MCP / gRPC paths share the same posture.
        var http = context.GetHttpContext();
        var sub = http?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? http?.User.Identity?.Name;
        if (string.IsNullOrEmpty(sub))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                "Authentication required: no subject id present on the caller's claims principal."));
        }
        return sub;
    }

    private static RpcException MapToRpcException(Exception ex) => ex switch
    {
        // RationaleRequiredException is a ValidationException — match the more
        // specific type first so the message-shape stays clear in the trailer.
        RationaleRequiredException r            => new RpcException(new Status(StatusCode.InvalidArgument, r.Message)),
        ValidationException v                   => new RpcException(new Status(StatusCode.InvalidArgument, v.Message)),
        NotFoundException n                     => new RpcException(new Status(StatusCode.NotFound, n.Message)),
        InvalidLifecycleTransitionException l   => new RpcException(new Status(StatusCode.FailedPrecondition, l.Message)),
        ConcurrentPublishException cp           => new RpcException(new Status(StatusCode.Aborted, cp.Message)),
        ConflictException c                     => new RpcException(new Status(StatusCode.AlreadyExists, c.Message)),
        DbUpdateConcurrencyException d          => new RpcException(new Status(StatusCode.Aborted, d.Message)),
        RpcException existing                   => existing,
        _ => throw new InvalidOperationException(
            $"Unmapped exception in LifecycleGrpcService: {ex.GetType().Name}", ex),
    };

    private static PolicyVersionMessage ToMessage(PolicyVersionDto dto)
    {
        var msg = new PolicyVersionMessage
        {
            Id = dto.Id.ToString(),
            PolicyId = dto.PolicyId.ToString(),
            Version = dto.Version,
            State = dto.State,
            Enforcement = dto.Enforcement,
            Severity = dto.Severity,
            Summary = dto.Summary,
            RulesJson = dto.RulesJson,
            CreatedAt = dto.CreatedAt.ToString("o"),
            CreatedBySubjectId = dto.CreatedBySubjectId,
            ProposerSubjectId = dto.ProposerSubjectId,
        };
        msg.Scopes.AddRange(dto.Scopes);
        return msg;
    }
}
