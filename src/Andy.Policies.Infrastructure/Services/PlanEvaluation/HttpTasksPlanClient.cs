// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Policies.Application.PlanEvaluation;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.Services.PlanEvaluation;

/// <summary>
/// Production <see cref="ITasksPlanClient"/> for
/// rivoli-ai/andy-policies#232. Pulls goal + tasks + plan-time
/// estimates from andy-tasks via the typed HttpClient wired in
/// <c>Program.cs</c> (the same Andy.Auth.M2MClient bearer handler
/// that the existing RBAC client uses).
/// </summary>
/// <remarks>
/// <para>
/// Three round-trips per evaluate-plan call: <c>GET /api/goals/{id}</c>,
/// <c>GET /api/goals/{id}/tasks</c>, <c>GET /api/goals/{id}/estimates</c>.
/// The estimates call is best-effort — a 404 (no estimates yet) is
/// mapped to <c>EstimatedCostUsd = null</c>, which causes the
/// <c>respectsCostBudget</c> predicate to surface
/// <c>data unavailable</c> rather than fault. Goal and tasks are
/// load-bearing: a 404 on the goal endpoint cascades to the controller
/// as a 404; a 404 on tasks (which can happen for goals whose plan
/// hasn't been materialised) maps to an empty task list, which
/// predicates evaluate against verbatim.
/// </para>
/// <para>
/// Workspace tier / approved-agents / cost-budget aren't on the
/// andy-tasks <c>GoalDto</c> today — they're workspace metadata that
/// lives on Conductor's workspace catalog. For #232 we project what
/// andy-tasks does expose: container id + repository + branch. The
/// other fields are left null, which causes the relevant predicates
/// to surface <c>data unavailable</c> until the workspace catalog
/// contract grows them. Future PRs will replace the null-fallbacks
/// with a real lookup against the workspace metadata service.
/// </para>
/// </remarks>
public sealed class HttpTasksPlanClient : ITasksPlanClient
{
    private static readonly JsonSerializerOptions WireFormat = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient _http;
    private readonly ILogger<HttpTasksPlanClient> _log;

    public HttpTasksPlanClient(HttpClient http, ILogger<HttpTasksPlanClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<PlanEvaluationGoalView?> FetchGoalViewAsync(
        Guid goalId, CancellationToken ct = default)
    {
        var goal = await GetOrNullAsync<GoalEnvelope>(
            $"api/goals/{goalId:D}", ct).ConfigureAwait(false);
        if (goal is null) return null;

        var tasks = await GetOrEmptyAsync<TaskEnvelope[]>(
            $"api/goals/{goalId:D}/tasks", ct).ConfigureAwait(false)
            ?? Array.Empty<TaskEnvelope>();

        var estimates = await GetOrNullAsync<EstimatesEnvelope>(
            $"api/goals/{goalId:D}/estimates", ct).ConfigureAwait(false);

        return new PlanEvaluationGoalView(
            GoalId: goal.Id,
            PlanVersion: goal.PlanVersion ?? "1",
            WorkspaceContainerId: goal.Workspace?.ContainerId,
            // Workspace tier / approvedAgentIds / costBudgetUsd aren't on
            // GoalDto today. Leaving them null surfaces the corresponding
            // predicates as "data unavailable" rather than silently
            // passing, per the issue spec.
            WorkspaceTier: null,
            WorkspaceApprovedAgentIds: null,
            WorkspaceCostBudgetUsd: null,
            Tasks: tasks.Select(MapTask).ToList(),
            EstimatedCostUsd: estimates?.Planned?.CostUsdP50);
    }

    private static PlanEvaluationTaskView MapTask(TaskEnvelope t) => new(
        TaskId: t.Id,
        // Effective agent: explicit executor first, then the top
        // suggestion. Mirrors how andy-tasks itself ranks agent
        // selection — the planner-suggested list is ordered.
        EffectiveAgentId: !string.IsNullOrWhiteSpace(t.ExecutorId)
            ? t.ExecutorId
            : t.SuggestedAgentIds?.FirstOrDefault(),
        // Preserve null as "data unavailable". An explicit empty
        // collection means the contract is present and permits no
        // tools; collapsing the states would approve incomplete data.
        ToolsAllowed: t.DelegationContract?.ToolsAllowed,
        TargetEnv: t.TargetEnv);

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        try
        {
            using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<T>(WireFormat, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex,
                "tasks plan client: GET {Path} failed transport; treating as missing", path);
            return null;
        }
    }

    private async Task<T?> GetOrEmptyAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        var v = await GetOrNullAsync<T>(path, ct).ConfigureAwait(false);
        return v;
    }

    // ---- Wire shapes (camelCase JSON, mirror andy-tasks DTOs) -------------

    private sealed record GoalEnvelope(
        Guid Id,
        string? Title,
        string? PlanVersion,
        WorkspaceEnvelope? Workspace);

    private sealed record WorkspaceEnvelope(
        string? ContainerId,
        string? Repository,
        string? Branch);

    private sealed record TaskEnvelope(
        Guid Id,
        string? ExternalId,
        DelegationContractEnvelope? DelegationContract,
        IReadOnlyList<string>? SuggestedAgentIds,
        string? ExecutorId,
        string? TargetEnv);

    private sealed record DelegationContractEnvelope(
        string? Objective,
        string? OutputFormat,
        IReadOnlyList<string>? ToolsAllowed,
        IReadOnlyList<string>? Boundaries);

    private sealed record EstimatesEnvelope(EstimateSlotEnvelope? Planned);

    private sealed record EstimateSlotEnvelope(
        decimal? CostUsdP50,
        decimal? CostUsdP90);
}
