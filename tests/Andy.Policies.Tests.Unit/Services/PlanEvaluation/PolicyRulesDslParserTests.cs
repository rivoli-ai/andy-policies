// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Services.PlanEvaluation;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services.PlanEvaluation;

/// <summary>
/// Unit tests for the policy <c>RulesJson</c> → <see cref="PlanEvaluationRules"/>
/// parser. The parser must tolerate malformed JSON and missing
/// <c>planEvaluation</c> blocks (returning null in both cases) without
/// throwing, because a single malformed policy in the catalog must
/// not break evaluation for every other policy.
/// </summary>
public class PolicyRulesDslParserTests
{
    [Fact]
    public void Returns_null_for_null_input()
    {
        PolicyRulesDslParser.TryParse(null).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_empty_input()
    {
        PolicyRulesDslParser.TryParse("   ").Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_malformed_json()
    {
        PolicyRulesDslParser.TryParse("{ this is not json").Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_planEvaluation_block_absent()
    {
        // Existing P1/V1 policies have allow/deny shapes but no
        // planEvaluation; the parser must not throw on them.
        var legacy = "{\"allow\": [\"read-file\"], \"deny\": []}";
        PolicyRulesDslParser.TryParse(legacy).Should().BeNull();
    }

    [Fact]
    public void Parses_decision_rules_with_requireAll()
    {
        var json = """
        {
          "planEvaluation": {
            "decisionRules": [
              { "decision": "approve", "requireAll": ["allTasksReadOnly", "workspaceIsSandbox"] }
            ]
          }
        }
        """;

        var rules = PolicyRulesDslParser.TryParse(json);

        rules.Should().NotBeNull();
        rules!.DecisionRules.Should().HaveCount(1);
        rules.DecisionRules![0].Decision.Should().Be("approve");
        rules.DecisionRules[0].RequireAll.Should().BeEquivalentTo("allTasksReadOnly", "workspaceIsSandbox");
    }

    [Fact]
    public void Parses_decision_rules_with_requireNone()
    {
        var json = """
        {
          "planEvaluation": {
            "decisionRules": [
              { "decision": "reject", "requireNone": ["workspaceIsSandbox"] }
            ]
          }
        }
        """;

        var rules = PolicyRulesDslParser.TryParse(json);

        rules!.DecisionRules![0].Decision.Should().Be("reject");
        rules.DecisionRules[0].RequireNone.Should().ContainSingle().Which.Should().Be("workspaceIsSandbox");
    }

    [Fact]
    public void Tolerates_case_insensitive_property_names()
    {
        // PascalCase, common when a developer hand-writes policy JSON
        // from a .NET DTO source. Parser is case-insensitive.
        var json = """
        {
          "PlanEvaluation": {
            "DecisionRules": [
              { "Decision": "approve", "RequireAll": ["allTasksReadOnly"] }
            ]
          }
        }
        """;

        var rules = PolicyRulesDslParser.TryParse(json);

        rules!.DecisionRules!.Should().HaveCount(1);
    }
}
