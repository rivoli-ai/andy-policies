// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.PlanEvaluation;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Docs;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services.PlanEvaluation;

/// <summary>
/// Unit tests for <see cref="ComplianceAuditPublisher"/>
/// (rivoli-ai/conductor#1945 — TX F7.2). Drives the publisher with a
/// capturing <see cref="IDocsClient"/> + a stub <see cref="IAuditExporter"/>
/// so the assertions focus on the request shape: the correct
/// <c>role:Audit</c> links (Goal-only for plan-finalize, Goal + Task for
/// per-task), the MIME types (<c>application/json</c> for the assessment,
/// the NDJSON segment), the disabled (shipped-dark) skip path, the
/// best-effort failure path (a docs.put failure degrades and never
/// throws), and a JSON round-trip back into the #1944 model.
/// </summary>
public sealed class ComplianceAuditPublisherTests
{
    private static readonly ComplianceAssessment SampleAssessment = new(
        Decision: "reject",
        RiskTier: RiskTier.Critical,
        RiskScore: 20,
        Violations: new[]
        {
            new ComplianceViolation("no-prod-deploy", "noProductionDeploy", "fail", "reject", "deploys to prod"),
        },
        Predicates: new[]
        {
            new PredicateResultDto("noProductionDeploy", false, "deploys to prod"),
        },
        EvaluatedAt: DateTimeOffset.Parse("2026-05-31T12:00:00Z"));

    [Fact]
    public async Task Disabled_publisher_skips_and_never_calls_docs()
    {
        var docs = new CapturingDocsClient();
        var pub = NewPublisher(docs, enabled: false);

        var result = await pub.PublishTaskAsync(
            Guid.NewGuid(), Guid.NewGuid(), "1", SampleAssessment);

        result.Skipped.Should().BeTrue();
        result.Degraded.Should().BeFalse();
        docs.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Plan_publish_links_goal_only_with_audit_role()
    {
        var docs = new CapturingDocsClient();
        var pub = NewPublisher(docs, enabled: true);
        var goalId = Guid.NewGuid();

        var result = await pub.PublishPlanAsync(goalId, "1", SampleAssessment);

        result.Skipped.Should().BeFalse();
        result.Degraded.Should().BeFalse();

        var assessmentReq = docs.Requests[0];
        assessmentReq.MimeType.Should().Be("application/json");
        assessmentReq.Links.Should().ContainSingle();
        assessmentReq.Links[0].TargetType.Should().Be(DocsLinkTargetType.Goal);
        assessmentReq.Links[0].TargetId.Should().Be(goalId.ToString("D"));
        assessmentReq.Links[0].Role.Should().Be("Audit");
    }

    [Fact]
    public async Task Task_publish_links_goal_and_task_with_audit_role()
    {
        var docs = new CapturingDocsClient();
        var pub = NewPublisher(docs, enabled: true);
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        await pub.PublishTaskAsync(goalId, taskId, "1", SampleAssessment);

        var links = docs.Requests[0].Links;
        links.Should().HaveCount(2);
        links.Should().Contain(l =>
            l.TargetType == DocsLinkTargetType.Goal && l.TargetId == goalId.ToString("D") && l.Role == "Audit");
        links.Should().Contain(l =>
            l.TargetType == DocsLinkTargetType.Task && l.TargetId == taskId.ToString("D") && l.Role == "Audit");
    }

    [Fact]
    public async Task Writes_both_assessment_and_audit_chain_segment_when_enabled()
    {
        var docs = new CapturingDocsClient();
        var pub = NewPublisher(docs, enabled: true, includeChain: true);

        var result = await pub.PublishTaskAsync(Guid.NewGuid(), Guid.NewGuid(), "1", SampleAssessment);

        docs.Requests.Should().HaveCount(2);
        // First the assessment JSON, then the NDJSON audit-chain segment.
        docs.Requests[0].MimeType.Should().Be("application/json");
        docs.Requests[0].FileName.Should().EndWith(".json");
        docs.Requests[1].FileName.Should().EndWith(".ndjson");
        result.AssessmentRef.Should().NotBeNull();
        result.AuditChainRef.Should().NotBeNull();
    }

    [Fact]
    public async Task IncludeAuditChainSegment_false_writes_assessment_only()
    {
        var docs = new CapturingDocsClient();
        var pub = NewPublisher(docs, enabled: true, includeChain: false);

        var result = await pub.PublishPlanAsync(Guid.NewGuid(), "1", SampleAssessment);

        docs.Requests.Should().ContainSingle();
        result.AssessmentRef.Should().NotBeNull();
        result.AuditChainRef.Should().BeNull();
    }

    [Fact]
    public async Task Assessment_payload_round_trips_into_the_1944_model()
    {
        var docs = new CapturingDocsClient();
        var pub = NewPublisher(docs, enabled: true);
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        await pub.PublishTaskAsync(goalId, taskId, "7", SampleAssessment);

        var json = Encoding.UTF8.GetString(docs.Requests[0].Content);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("goalId").GetGuid().Should().Be(goalId);
        root.GetProperty("taskId").GetGuid().Should().Be(taskId);
        root.GetProperty("planVersion").GetString().Should().Be("7");

        // The embedded assessment deserialises back into the #1944 record.
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var back = root.GetProperty("assessment").Deserialize<ComplianceAssessment>(options);
        back.Should().NotBeNull();
        back!.Decision.Should().Be("reject");
        back.RiskTier.Should().Be(RiskTier.Critical);
        back.RiskScore.Should().Be(20);
        back.Violations.Should().ContainSingle();
        back.Violations[0].PolicyKey.Should().Be("no-prod-deploy");
    }

    [Fact]
    public async Task Docs_put_failure_degrades_and_does_not_throw()
    {
        var docs = new ThrowingDocsClient();
        var pub = NewPublisher(docs, enabled: true);

        var result = await pub.PublishTaskAsync(Guid.NewGuid(), Guid.NewGuid(), "1", SampleAssessment);

        result.Skipped.Should().BeFalse();
        result.Degraded.Should().BeTrue();
        result.AssessmentRef.Should().BeNull();
    }

    [Fact]
    public async Task Audit_chain_failure_keeps_the_assessment_ref_but_degrades()
    {
        // First put (assessment) succeeds; second put (chain segment) throws.
        var docs = new FailSecondPutDocsClient();
        var pub = NewPublisher(docs, enabled: true, includeChain: true);

        var result = await pub.PublishPlanAsync(Guid.NewGuid(), "1", SampleAssessment);

        result.Degraded.Should().BeTrue();
        result.AssessmentRef.Should().NotBeNull("the assessment was written before the chain segment failed");
        result.AuditChainRef.Should().BeNull();
    }

    // ---- helpers ----------------------------------------------------------

    private static ComplianceAuditPublisher NewPublisher(
        IDocsClient docs, bool enabled, bool includeChain = true)
    {
        var db = InMemoryDbFixture.Create();
        var options = Options.Create(new ComplianceAuditPublisherOptions
        {
            Enabled = enabled,
            IncludeAuditChainSegment = includeChain,
        });
        return new ComplianceAuditPublisher(
            docs,
            new StubAuditExporter(),
            db,
            options,
            NullLogger<ComplianceAuditPublisher>.Instance);
    }

    private sealed class CapturingDocsClient : IDocsClient
    {
        public List<DocsPutRequest> Requests { get; } = new();

        public Task<DocsRef> PutAsync(DocsPutRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DocsRef(
                Guid.NewGuid(), Guid.NewGuid(), "hash", request.Content.LongLength,
                Array.Empty<Guid>()));
        }
    }

    private sealed class ThrowingDocsClient : IDocsClient
    {
        public Task<DocsRef> PutAsync(DocsPutRequest request, CancellationToken ct = default)
            => throw new DocsPutException("[ANDY-POLICIES-DOCS-PUT-1] boom");
    }

    private sealed class FailSecondPutDocsClient : IDocsClient
    {
        private int _calls;

        public Task<DocsRef> PutAsync(DocsPutRequest request, CancellationToken ct = default)
        {
            if (++_calls == 1)
            {
                return Task.FromResult(new DocsRef(
                    Guid.NewGuid(), Guid.NewGuid(), "hash", 0, Array.Empty<Guid>()));
            }
            throw new DocsPutException("[ANDY-POLICIES-DOCS-PUT-1] chain segment boom");
        }
    }

    private sealed class StubAuditExporter : IAuditExporter
    {
        public async Task WriteNdjsonAsync(Stream output, long? fromSeq, long? toSeq, CancellationToken ct)
        {
            var line = $"{{\"type\":\"summary\",\"fromSeq\":0,\"toSeq\":{toSeq ?? 0},\"count\":0}}\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            await output.WriteAsync(bytes, ct);
        }
    }
}
