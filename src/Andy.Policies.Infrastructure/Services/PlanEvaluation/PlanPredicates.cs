// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.PlanEvaluation;

namespace Andy.Policies.Infrastructure.Services.PlanEvaluation;

/// <summary>
/// Five plan-aware predicates pinned by the rivoli-ai/andy-policies#232
/// acceptance criteria. Each predicate is a pure function of the
/// projected <see cref="PlanEvaluationGoalView"/>; predicates whose
/// required data is null on the view return
/// <see cref="PredicateEvaluation.Unevaluable"/> so the evaluator can
/// surface them as <c>passed: false, reason: "data unavailable"</c>
/// rather than silently passing.
/// </summary>
public static class PlanPredicateNames
{
    public const string AllTasksReadOnly = "allTasksReadOnly";
    public const string WorkspaceIsSandbox = "workspaceIsSandbox";
    public const string NoProductionDeploy = "noProductionDeploy";
    public const string AllAgentsApproved = "allAgentsApproved";
    public const string RespectsCostBudget = "respectsCostBudget";
}

/// <summary>
/// Tools whose name suggests filesystem write, container exec, or
/// shell exec semantics. Curated rather than allow-listed because the
/// andy-tasks tool catalog is open-ended — any predicate that defaults
/// to "approve when unknown" would be a security regression. Match is
/// case-insensitive substring on the tool slug.
/// </summary>
internal static class WriteOrExecToolHeuristics
{
    private static readonly string[] WriteOrExecMarkers =
    [
        "write", "exec", "shell", "run", "delete", "remove",
        "edit", "patch", "create", "mutate", "deploy",
        "publish", "push",
    ];

    public static bool LooksWriteOrExec(string tool)
    {
        var t = tool.ToLowerInvariant();
        foreach (var marker in WriteOrExecMarkers)
        {
            if (t.Contains(marker, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

public sealed class AllTasksReadOnlyPredicate : IPlanPredicate
{
    public string Name => PlanPredicateNames.AllTasksReadOnly;

    public PredicateEvaluation Evaluate(PlanEvaluationGoalView view)
    {
        if (view.Tasks.Count == 0)
        {
            // A plan with zero tasks is vacuously read-only — there are
            // no write-or-exec tools because there are no tasks. But
            // approving a zero-task plan would be unusual; flagging it
            // here is misleading. Pass with explicit reason.
            return PredicateEvaluation.Pass("no tasks in plan");
        }

        var offenders = view.Tasks
            .Where(t => t.ToolsAllowed.Any(WriteOrExecToolHeuristics.LooksWriteOrExec))
            .Select(t => t.TaskId.ToString())
            .ToList();

        return offenders.Count == 0
            ? PredicateEvaluation.Pass("no task carries write/exec tools")
            : PredicateEvaluation.Fail(
                $"tasks with write/exec tools: {string.Join(", ", offenders)}");
    }
}

public sealed class WorkspaceIsSandboxPredicate : IPlanPredicate
{
    public string Name => PlanPredicateNames.WorkspaceIsSandbox;

    public PredicateEvaluation Evaluate(PlanEvaluationGoalView view)
    {
        if (string.IsNullOrWhiteSpace(view.WorkspaceTier))
        {
            return PredicateEvaluation.Unevaluable();
        }

        return string.Equals(view.WorkspaceTier, "sandbox", StringComparison.OrdinalIgnoreCase)
            ? PredicateEvaluation.Pass($"workspace tier is {view.WorkspaceTier}")
            : PredicateEvaluation.Fail($"workspace tier is {view.WorkspaceTier}, not sandbox");
    }
}

public sealed class NoProductionDeployPredicate : IPlanPredicate
{
    private static readonly string[] ProdMarkers = ["prod", "production"];

    public string Name => PlanPredicateNames.NoProductionDeploy;

    public PredicateEvaluation Evaluate(PlanEvaluationGoalView view)
    {
        if (view.Tasks.Count == 0)
        {
            return PredicateEvaluation.Pass("no tasks in plan");
        }

        var prodTasks = view.Tasks
            .Where(t => t.TargetEnv is not null &&
                        ProdMarkers.Any(m => string.Equals(t.TargetEnv, m,
                                              StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.TaskId.ToString())
            .ToList();

        return prodTasks.Count == 0
            ? PredicateEvaluation.Pass("no task targets prod")
            : PredicateEvaluation.Fail(
                $"tasks targeting prod: {string.Join(", ", prodTasks)}");
    }
}

public sealed class AllAgentsApprovedPredicate : IPlanPredicate
{
    public string Name => PlanPredicateNames.AllAgentsApproved;

    public PredicateEvaluation Evaluate(PlanEvaluationGoalView view)
    {
        if (view.WorkspaceApprovedAgentIds is null)
        {
            return PredicateEvaluation.Unevaluable();
        }

        if (view.Tasks.Count == 0)
        {
            return PredicateEvaluation.Pass("no tasks in plan");
        }

        // Build a case-sensitive set — agent IDs are opaque slugs;
        // case-insensitive matching would conflate distinct agents.
        var approved = new HashSet<string>(view.WorkspaceApprovedAgentIds, StringComparer.Ordinal);

        var unapproved = new List<string>();
        foreach (var task in view.Tasks)
        {
            if (task.EffectiveAgentId is null)
            {
                // A task without any selected/suggested agent cannot be
                // proven approved. Per the spec contract, missing data
                // does not pass — fail rather than skip.
                unapproved.Add($"{task.TaskId} (no agent)");
                continue;
            }

            if (!approved.Contains(task.EffectiveAgentId))
            {
                unapproved.Add($"{task.TaskId} ({task.EffectiveAgentId})");
            }
        }

        return unapproved.Count == 0
            ? PredicateEvaluation.Pass("every task's agent is in the approved list")
            : PredicateEvaluation.Fail(
                $"unapproved agents: {string.Join(", ", unapproved)}");
    }
}

public sealed class RespectsCostBudgetPredicate : IPlanPredicate
{
    public string Name => PlanPredicateNames.RespectsCostBudget;

    public PredicateEvaluation Evaluate(PlanEvaluationGoalView view)
    {
        if (view.WorkspaceCostBudgetUsd is null || view.EstimatedCostUsd is null)
        {
            return PredicateEvaluation.Unevaluable();
        }

        return view.EstimatedCostUsd.Value < view.WorkspaceCostBudgetUsd.Value
            ? PredicateEvaluation.Pass(
                $"estimated ${view.EstimatedCostUsd.Value:0.##} < budget ${view.WorkspaceCostBudgetUsd.Value:0.##}")
            : PredicateEvaluation.Fail(
                $"estimated ${view.EstimatedCostUsd.Value:0.##} ≥ budget ${view.WorkspaceCostBudgetUsd.Value:0.##}");
    }
}
