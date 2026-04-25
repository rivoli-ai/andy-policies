// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Api.Protos;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.Queries;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Api.GrpcServices;

/// <summary>
/// gRPC surface for the policy catalog (P1.7, story rivoli-ai/andy-policies#77).
/// Delegates to <see cref="IPolicyService"/> — the same service powering REST
/// (P1.5) and MCP (P1.6). Service exceptions are translated to gRPC status
/// codes per the table below; the equivalent HTTP mappings live in
/// <c>PolicyExceptionHandler</c>.
///
/// <list type="table">
///   <listheader><term>Exception</term><description>gRPC status / HTTP equivalent</description></listheader>
///   <item><term><see cref="ValidationException"/></term><description>InvalidArgument / 400</description></item>
///   <item><term><see cref="NotFoundException"/></term><description>NotFound / 404</description></item>
///   <item><term><see cref="ConflictException"/></term><description>AlreadyExists / 409</description></item>
///   <item><term><see cref="DbUpdateConcurrencyException"/></term><description>Aborted / 412</description></item>
/// </list>
/// </summary>
[Authorize]
public class PolicyGrpcService : Protos.PolicyService.PolicyServiceBase
{
    private readonly IPolicyService _policies;

    public PolicyGrpcService(IPolicyService policies)
    {
        _policies = policies;
    }

    // -- read RPCs ------------------------------------------------------------

    public override async Task<ListPoliciesResponse> ListPolicies(ListPoliciesRequest request, ServerCallContext context)
    {
        try
        {
            var query = new ListPoliciesQuery(
                NamePrefix: NullIfEmpty(request.NamePrefix),
                Scope: NullIfEmpty(request.Scope),
                Enforcement: NullIfEmpty(request.Enforcement),
                Severity: NullIfEmpty(request.Severity),
                Skip: request.Skip,
                Take: request.Take == 0 ? 100 : request.Take);

            var results = await _policies.ListPoliciesAsync(query, context.CancellationToken);

            var response = new ListPoliciesResponse();
            response.Policies.AddRange(results.Select(ToMessage));
            return response;
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override async Task<PolicyResponse> GetPolicy(GetPolicyRequest request, ServerCallContext context)
    {
        var id = ParseGuidOrThrow(request.Id, "id");
        var policy = await _policies.GetPolicyAsync(id, context.CancellationToken);
        if (policy is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Policy {id} not found."));
        return new PolicyResponse { Policy = ToMessage(policy) };
    }

    public override async Task<PolicyResponse> GetPolicyByName(GetPolicyByNameRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "name is required."));

        var policy = await _policies.GetPolicyByNameAsync(request.Name, context.CancellationToken);
        if (policy is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Policy '{request.Name}' not found."));
        return new PolicyResponse { Policy = ToMessage(policy) };
    }

    public override async Task<ListVersionsResponse> ListVersions(ListVersionsRequest request, ServerCallContext context)
    {
        var policyId = ParseGuidOrThrow(request.PolicyId, "policy_id");
        var versions = await _policies.ListVersionsAsync(policyId, context.CancellationToken);
        var response = new ListVersionsResponse();
        response.Versions.AddRange(versions.Select(ToMessage));
        return response;
    }

    public override async Task<PolicyVersionResponse> GetVersion(GetVersionRequest request, ServerCallContext context)
    {
        var policyId = ParseGuidOrThrow(request.PolicyId, "policy_id");
        var versionId = ParseGuidOrThrow(request.VersionId, "version_id");
        var version = await _policies.GetVersionAsync(policyId, versionId, context.CancellationToken);
        if (version is null)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"Version {versionId} not found under policy {policyId}."));
        return new PolicyVersionResponse { Version = ToMessage(version) };
    }

    public override async Task<PolicyVersionResponse> GetActiveVersion(GetActiveVersionRequest request, ServerCallContext context)
    {
        var policyId = ParseGuidOrThrow(request.PolicyId, "policy_id");
        var version = await _policies.GetActiveVersionAsync(policyId, context.CancellationToken);
        if (version is null)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"Policy {policyId} has no active version."));
        return new PolicyVersionResponse { Version = ToMessage(version) };
    }

    // -- draft mutation RPCs --------------------------------------------------

    public override async Task<PolicyVersionResponse> CreateDraft(CreateDraftRequest request, ServerCallContext context)
    {
        try
        {
            var dto = await _policies.CreateDraftAsync(
                new CreatePolicyRequest(
                    Name: request.Name,
                    Description: NullIfEmpty(request.Description),
                    Summary: request.Summary,
                    Enforcement: request.Enforcement,
                    Severity: request.Severity,
                    Scopes: request.Scopes.ToList(),
                    RulesJson: request.RulesJson),
                ResolveSubjectId(context),
                context.CancellationToken);

            return new PolicyVersionResponse { Version = ToMessage(dto) };
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override async Task<PolicyVersionResponse> UpdateDraft(UpdateDraftRequest request, ServerCallContext context)
    {
        var policyId = ParseGuidOrThrow(request.PolicyId, "policy_id");
        var versionId = ParseGuidOrThrow(request.VersionId, "version_id");
        try
        {
            var dto = await _policies.UpdateDraftAsync(
                policyId, versionId,
                new UpdatePolicyVersionRequest(
                    Summary: request.Summary,
                    Enforcement: request.Enforcement,
                    Severity: request.Severity,
                    Scopes: request.Scopes.ToList(),
                    RulesJson: request.RulesJson),
                ResolveSubjectId(context),
                context.CancellationToken);

            return new PolicyVersionResponse { Version = ToMessage(dto) };
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    public override async Task<PolicyVersionResponse> BumpDraft(BumpDraftRequest request, ServerCallContext context)
    {
        var policyId = ParseGuidOrThrow(request.PolicyId, "policy_id");
        var sourceVersionId = ParseGuidOrThrow(request.SourceVersionId, "source_version_id");
        try
        {
            var dto = await _policies.BumpDraftFromVersionAsync(
                policyId, sourceVersionId, ResolveSubjectId(context), context.CancellationToken);

            return new PolicyVersionResponse { Version = ToMessage(dto) };
        }
        catch (Exception ex) { throw MapToRpcException(ex); }
    }

    // -- helpers --------------------------------------------------------------

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

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
        // Mirrors PoliciesController + ItemsGrpcService: prefer the
        // authenticated principal name, fall back to a labeled placeholder.
        var http = context.GetHttpContext();
        return http?.User.Identity?.Name ?? "grpc-anonymous";
    }

    private static RpcException MapToRpcException(Exception ex) => ex switch
    {
        ValidationException v          => new RpcException(new Status(StatusCode.InvalidArgument, v.Message)),
        NotFoundException n            => new RpcException(new Status(StatusCode.NotFound, n.Message)),
        ConflictException c            => new RpcException(new Status(StatusCode.AlreadyExists, c.Message)),
        DbUpdateConcurrencyException d => new RpcException(new Status(StatusCode.Aborted, d.Message)),
        _ => throw new InvalidOperationException(
            $"Unmapped exception in PolicyGrpcService: {ex.GetType().Name}", ex),
    };

    private static PolicyMessage ToMessage(PolicyDto dto)
    {
        var msg = new PolicyMessage
        {
            Id = dto.Id.ToString(),
            Name = dto.Name,
            CreatedAt = dto.CreatedAt.ToString("o"),
            CreatedBySubjectId = dto.CreatedBySubjectId,
            VersionCount = dto.VersionCount,
        };
        if (dto.Description is not null) msg.Description = dto.Description;
        if (dto.ActiveVersionId is not null) msg.ActiveVersionId = dto.ActiveVersionId.Value.ToString();
        return msg;
    }

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
