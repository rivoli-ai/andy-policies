// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.Settings;
using Andy.Policies.Domain.Enums;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using DomainScopeKind = Andy.Policies.Domain.Enums.OverrideScopeKind;
using DomainEffect = Andy.Policies.Domain.Enums.OverrideEffect;
using DomainState = Andy.Policies.Domain.Enums.OverrideState;
using DtoProposeRequest = Andy.Policies.Application.Dtos.ProposeOverrideRequest;
using DtoRevokeRequest = Andy.Policies.Application.Dtos.RevokeOverrideRequest;
using ProtoProposeRequest = Andy.Policies.Api.Protos.ProposeOverrideRequest;
using ProtoApproveRequest = Andy.Policies.Api.Protos.ApproveOverrideRequest;
using ProtoRevokeRequest = Andy.Policies.Api.Protos.RevokeOverrideRequest;
using ProtoListRequest = Andy.Policies.Api.Protos.ListOverridesRequest;
using ProtoListResponse = Andy.Policies.Api.Protos.ListOverridesResponse;
using ProtoGetRequest = Andy.Policies.Api.Protos.GetOverrideRequest;
using ProtoActiveRequest = Andy.Policies.Api.Protos.GetActiveOverridesRequest;

namespace Andy.Policies.Api.GrpcServices;

/// <summary>
/// gRPC surface for the override workflow (P5.7, story
/// rivoli-ai/andy-policies#60). Six RPCs delegate to the same
/// <see cref="IOverrideService"/> powering REST (P5.5, #58) and MCP
/// (P5.6, #59). Service exceptions translate to gRPC status codes
/// per the table below; the equivalent HTTP mappings live in
/// <c>PolicyExceptionHandler</c> (with parallel <c>errorCode</c>
/// extensions) and the MCP layer's prefixed error strings:
///
/// <list type="table">
///   <listheader><term>Exception / condition</term><description>gRPC status</description></listheader>
///   <item><term>Settings gate off</term><description><c>PERMISSION_DENIED</c> + trailer <c>override_disabled=1</c></description></item>
///   <item><term><see cref="SelfApprovalException"/></term><description><c>PERMISSION_DENIED</c> + trailer <c>reason=self_approval</c></description></item>
///   <item><term><see cref="RbacDeniedException"/></term><description><c>PERMISSION_DENIED</c> + trailer <c>reason=forbidden</c></description></item>
///   <item><term><see cref="NotFoundException"/></term><description><c>NOT_FOUND</c></description></item>
///   <item><term><see cref="ConflictException"/></term><description><c>FAILED_PRECONDITION</c></description></item>
///   <item><term><see cref="ValidationException"/></term><description><c>INVALID_ARGUMENT</c></description></item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Authentication firewall:</b> mutating RPCs require an authenticated
/// caller. If the gRPC request reaches this service with no
/// <c>NameIdentifier</c> / <c>Name</c> claim the RPC throws
/// <c>UNAUTHENTICATED</c> rather than writing a fallback subject id
/// into the catalog (mirrors the REST controller — see #13). Reads
/// (<see cref="ListOverrides"/>, <see cref="GetOverride"/>,
/// <see cref="GetActiveOverrides"/>) bypass the gate so the resolution
/// algorithm (P4.3) and Conductor admission keep working when
/// <c>andy.policies.experimentalOverridesEnabled</c> is off.
/// </remarks>
[Authorize]
public class OverridesGrpcService : Andy.Policies.Api.Protos.OverridesService.OverridesServiceBase
{
    /// <summary>Trailer key set on PERMISSION_DENIED responses when
    /// the settings gate is off. Surface-parity contract with REST
    /// (errorCode <c>override.disabled</c>) and MCP
    /// (<c>policy.override.disabled</c>).</summary>
    public const string GateDisabledTrailer = "override_disabled";

    /// <summary>Trailer key set on PERMISSION_DENIED responses when
    /// the failure is self-approval, RBAC denial, or another
    /// per-action authorization rejection. Value is one of
    /// <c>self_approval</c>, <c>forbidden</c>.</summary>
    public const string ReasonTrailer = "reason";

    private readonly IOverrideService _service;
    private readonly IExperimentalOverridesGate _gate;
    private readonly IHttpContextAccessor _http;

    public OverridesGrpcService(
        IOverrideService service,
        IExperimentalOverridesGate gate,
        IHttpContextAccessor http)
    {
        _service = service;
        _gate = gate;
        _http = http;
    }

    public override async Task<OverrideMessage> ProposeOverride(
        ProtoProposeRequest request, ServerCallContext context)
    {
        EnforceGateOrThrow();
        var subject = ResolveSubjectOrThrow();

        if (!Guid.TryParse(request.PolicyVersionId, out var pvid))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"policy_version_id '{request.PolicyVersionId}' is not a valid GUID."));
        }
        var scopeKind = ToDomainScopeKind(request.ScopeKind);
        var effect = ToDomainEffect(request.Effect);
        Guid? replacementId = null;
        if (!string.IsNullOrEmpty(request.ReplacementPolicyVersionId))
        {
            if (!Guid.TryParse(request.ReplacementPolicyVersionId, out var rid))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"replacement_policy_version_id '{request.ReplacementPolicyVersionId}' is not a valid GUID."));
            }
            replacementId = rid;
        }
        if (!DateTimeOffset.TryParse(request.ExpiresAt, out var expiresAt))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"expires_at '{request.ExpiresAt}' is not a valid ISO 8601 timestamp."));
        }

        try
        {
            var dto = await _service.ProposeAsync(
                new DtoProposeRequest(pvid, scopeKind, request.ScopeRef, effect,
                    replacementId, expiresAt, request.Rationale),
                subject, context.CancellationToken).ConfigureAwait(false);
            return ToMessage(dto);
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override async Task<OverrideMessage> ApproveOverride(
        ProtoApproveRequest request, ServerCallContext context)
    {
        EnforceGateOrThrow();
        var subject = ResolveSubjectOrThrow();
        var id = ParseGuidOrThrow(request.Id, "id");

        try
        {
            var dto = await _service.ApproveAsync(id, subject, context.CancellationToken)
                .ConfigureAwait(false);
            return ToMessage(dto);
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override async Task<OverrideMessage> RevokeOverride(
        ProtoRevokeRequest request, ServerCallContext context)
    {
        EnforceGateOrThrow();
        var subject = ResolveSubjectOrThrow();
        var id = ParseGuidOrThrow(request.Id, "id");

        try
        {
            var dto = await _service.RevokeAsync(
                id, new DtoRevokeRequest(request.RevocationReason),
                subject, context.CancellationToken).ConfigureAwait(false);
            return ToMessage(dto);
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override async Task<ProtoListResponse> ListOverrides(
        ProtoListRequest request, ServerCallContext context)
    {
        DomainState? state = request.State == ProtoOverrideState.OverrideStateUnspecified
            ? null
            : ToDomainState(request.State);
        DomainScopeKind? scopeKind = request.ScopeKind == ProtoScopeKind.ScopeKindUnspecified
            ? null
            : ToDomainScopeKind(request.ScopeKind);
        Guid? pvid = null;
        if (!string.IsNullOrEmpty(request.PolicyVersionId))
        {
            pvid = ParseGuidOrThrow(request.PolicyVersionId, "policy_version_id");
        }
        var scopeRef = string.IsNullOrEmpty(request.ScopeRef) ? null : request.ScopeRef;

        var rows = await _service.ListAsync(
            new OverrideListFilter(state, scopeKind, scopeRef, pvid), context.CancellationToken)
            .ConfigureAwait(false);
        var response = new ProtoListResponse();
        response.Items.AddRange(rows.Select(ToMessage));
        return response;
    }

    public override async Task<OverrideMessage> GetOverride(
        ProtoGetRequest request, ServerCallContext context)
    {
        var id = ParseGuidOrThrow(request.Id, "id");
        var dto = await _service.GetAsync(id, context.CancellationToken).ConfigureAwait(false);
        if (dto is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Override {id} not found."));
        }
        return ToMessage(dto);
    }

    public override async Task<ProtoListResponse> GetActiveOverrides(
        ProtoActiveRequest request, ServerCallContext context)
    {
        if (request.ScopeKind == ProtoScopeKind.ScopeKindUnspecified)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "scope_kind is required (SCOPE_KIND_UNSPECIFIED is not a valid value)."));
        }
        if (string.IsNullOrEmpty(request.ScopeRef))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "scope_ref is required."));
        }
        var rows = await _service.GetActiveAsync(
            ToDomainScopeKind(request.ScopeKind), request.ScopeRef, context.CancellationToken)
            .ConfigureAwait(false);
        var response = new ProtoListResponse();
        response.Items.AddRange(rows.Select(ToMessage));
        return response;
    }

    // -- helpers --------------------------------------------------------------

    private void EnforceGateOrThrow()
    {
        if (_gate.IsEnabled) return;
        var trailers = new Metadata
        {
            { GateDisabledTrailer, "1" },
        };
        throw new RpcException(
            new Status(StatusCode.PermissionDenied,
                "Experimental overrides are disabled via andy.policies.experimentalOverridesEnabled."),
            trailers);
    }

    private string ResolveSubjectOrThrow()
    {
        var user = _http.HttpContext?.User;
        var subject = user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.Identity?.Name;
        if (string.IsNullOrEmpty(subject))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                "Authentication required: no subject id present on the caller's claims principal."));
        }
        return subject;
    }

    private static Guid ParseGuidOrThrow(string raw, string field)
    {
        if (!Guid.TryParse(raw, out var guid))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{field} '{raw}' is not a valid GUID."));
        }
        return guid;
    }

    private static DomainScopeKind ToDomainScopeKind(ProtoScopeKind wire) => wire switch
    {
        ProtoScopeKind.ScopeKindPrincipal => DomainScopeKind.Principal,
        ProtoScopeKind.ScopeKindCohort => DomainScopeKind.Cohort,
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
            "scope_kind is required (SCOPE_KIND_UNSPECIFIED is not a valid value).")),
    };

    private static DomainEffect ToDomainEffect(ProtoEffectKind wire) => wire switch
    {
        ProtoEffectKind.EffectKindExempt => DomainEffect.Exempt,
        ProtoEffectKind.EffectKindReplace => DomainEffect.Replace,
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
            "effect is required (EFFECT_KIND_UNSPECIFIED is not a valid value).")),
    };

    private static DomainState ToDomainState(ProtoOverrideState wire) => wire switch
    {
        ProtoOverrideState.OverrideStateProposed => DomainState.Proposed,
        ProtoOverrideState.OverrideStateApproved => DomainState.Approved,
        ProtoOverrideState.OverrideStateRevoked => DomainState.Revoked,
        ProtoOverrideState.OverrideStateExpired => DomainState.Expired,
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
            "state is required (OVERRIDE_STATE_UNSPECIFIED is not a valid value).")),
    };

    private static ProtoScopeKind ToProtoScopeKind(DomainScopeKind domain) => domain switch
    {
        DomainScopeKind.Principal => ProtoScopeKind.ScopeKindPrincipal,
        DomainScopeKind.Cohort => ProtoScopeKind.ScopeKindCohort,
        _ => throw new InvalidOperationException($"Unknown OverrideScopeKind: {domain}"),
    };

    private static ProtoEffectKind ToProtoEffect(DomainEffect domain) => domain switch
    {
        DomainEffect.Exempt => ProtoEffectKind.EffectKindExempt,
        DomainEffect.Replace => ProtoEffectKind.EffectKindReplace,
        _ => throw new InvalidOperationException($"Unknown OverrideEffect: {domain}"),
    };

    private static ProtoOverrideState ToProtoState(DomainState domain) => domain switch
    {
        DomainState.Proposed => ProtoOverrideState.OverrideStateProposed,
        DomainState.Approved => ProtoOverrideState.OverrideStateApproved,
        DomainState.Revoked => ProtoOverrideState.OverrideStateRevoked,
        DomainState.Expired => ProtoOverrideState.OverrideStateExpired,
        _ => throw new InvalidOperationException($"Unknown OverrideState: {domain}"),
    };

    private static OverrideMessage ToMessage(OverrideDto dto) => new()
    {
        Id = dto.Id.ToString(),
        PolicyVersionId = dto.PolicyVersionId.ToString(),
        ScopeKind = ToProtoScopeKind(dto.ScopeKind),
        ScopeRef = dto.ScopeRef,
        Effect = ToProtoEffect(dto.Effect),
        ReplacementPolicyVersionId = dto.ReplacementPolicyVersionId?.ToString() ?? string.Empty,
        ProposerSubjectId = dto.ProposerSubjectId,
        ApproverSubjectId = dto.ApproverSubjectId ?? string.Empty,
        State = ToProtoState(dto.State),
        ProposedAt = dto.ProposedAt.ToString("o"),
        ApprovedAt = dto.ApprovedAt?.ToString("o") ?? string.Empty,
        ExpiresAt = dto.ExpiresAt.ToString("o"),
        Rationale = dto.Rationale,
        RevocationReason = dto.RevocationReason ?? string.Empty,
    };

    private static RpcException MapToRpcException(Exception ex) => ex switch
    {
        SelfApprovalException sax =>
            new RpcException(
                new Status(StatusCode.PermissionDenied, sax.Message),
                new Metadata { { ReasonTrailer, "self_approval" } }),
        RbacDeniedException rdx =>
            new RpcException(
                new Status(StatusCode.PermissionDenied, rdx.Message),
                new Metadata { { ReasonTrailer, "forbidden" } }),
        ValidationException v =>
            new RpcException(new Status(StatusCode.InvalidArgument, v.Message)),
        NotFoundException n =>
            new RpcException(new Status(StatusCode.NotFound, n.Message)),
        ConflictException c =>
            new RpcException(new Status(StatusCode.FailedPrecondition, c.Message)),
        RpcException existing => existing,
        _ => throw new InvalidOperationException(
            $"Unmapped exception in OverridesGrpcService: {ex.GetType().Name}", ex),
    };
}

