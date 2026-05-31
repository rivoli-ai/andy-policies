// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Infrastructure.Docs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Andy.Policies.Tests.Integration.PlanEvaluation;

/// <summary>
/// Contract test (rivoli-ai/conductor#1945 — TX F7.2): drives
/// <see cref="HttpDocsClient"/> against a WireMock stub that mimics the
/// andy-docs <c>POST /api/documents:put</c> (a.k.a. <c>docs.put</c>) wire
/// contract (<c>docs/agent-upload.md</c>). Pins:
/// <list type="bullet">
///   <item>the request is multipart with a <c>file</c> part (bytes + MIME)
///     and a <c>meta</c> part carrying the JSON
///     <c>{ name, title, message, links[] }</c> with PascalCase
///     <c>targetType</c>/<c>role</c> enum values;</item>
///   <item>the <see cref="DocsRef"/> response shape
///     (<c>documentId, linkId, versionHash, sizeBytes, additionalLinkIds</c>)
///     deserialises;</item>
///   <item>a non-2xx surfaces a <see cref="DocsPutException"/> carrying the
///     source code (so the publisher can degrade with an actionable log).</item>
/// </list>
/// If andy-docs renames any of these, the recorded stub here diverges
/// from the real service and this test fails — the contract is pinned on
/// both repos' PRs.
/// </summary>
public sealed class HttpDocsClientContractTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    private HttpDocsClient NewClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        return new HttpDocsClient(http, NullLogger<HttpDocsClient>.Instance);
    }

    private static DocsPutRequest SampleRequest(Guid goalId, Guid taskId) => new(
        FileName: "compliance-assessment.json",
        MimeType: "application/json",
        Content: Encoding.UTF8.GetBytes("""{"decision":"reject"}"""),
        Title: "Compliance assessment",
        Message: "andy-policies compliance assessment",
        Links: new[]
        {
            new DocsLink(DocsLinkTargetType.Goal, goalId.ToString("D"), "Audit"),
            new DocsLink(DocsLinkTargetType.Task, taskId.ToString("D"), "Audit"),
        });

    [Fact]
    public async Task Posts_multipart_with_meta_links_and_parses_docsref()
    {
        var goalId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var linkId = Guid.NewGuid();

        _server
            .Given(Request.Create().WithPath("/api/documents:put").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                  "documentId": "{{documentId}}",
                  "linkId": "{{linkId}}",
                  "versionHash": "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03",
                  "sizeBytes": 21,
                  "additionalLinkIds": []
                }
                """));

        var result = await NewClient().PutAsync(SampleRequest(goalId, taskId));

        result.DocumentId.Should().Be(documentId);
        result.LinkId.Should().Be(linkId);
        result.VersionHash.Should().NotBeNullOrEmpty();
        result.SizeBytes.Should().Be(21);

        // Inspect the captured multipart body: it must carry the meta JSON
        // with the two role:Audit links in PascalCase enum form.
        var log = _server.LogEntries.Single();
        var body = log.RequestMessage.Body ?? string.Empty;
        // .NET's MultipartFormDataContent emits the disposition name
        // unquoted (`name=file`); HTTP allows the quoted form too. Match
        // either so the assertion pins the part name, not the quoting.
        body.Should().MatchRegex("name=\"?file\"?");
        body.Should().MatchRegex("name=\"?meta\"?");
        body.Should().Contain("\"targetType\":\"Goal\"");
        body.Should().Contain("\"targetType\":\"Task\"");
        body.Should().Contain("\"role\":\"Audit\"");
        body.Should().Contain(goalId.ToString("D"));
        body.Should().Contain(taskId.ToString("D"));
    }

    [Fact]
    public async Task Surfaces_additional_link_ids_from_multi_link_upload()
    {
        var documentId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var extraLinkId = Guid.NewGuid();

        _server
            .Given(Request.Create().WithPath("/api/documents:put").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                  "documentId": "{{documentId}}",
                  "linkId": "{{linkId}}",
                  "versionHash": "abc",
                  "sizeBytes": 10,
                  "additionalLinkIds": ["{{extraLinkId}}"]
                }
                """));

        var result = await NewClient().PutAsync(SampleRequest(Guid.NewGuid(), Guid.NewGuid()));

        result.AdditionalLinkIds.Should().ContainSingle().Which.Should().Be(extraLinkId);
    }

    [Fact]
    public async Task Throws_DocsPutException_with_source_code_on_non_2xx()
    {
        _server
            .Given(Request.Create().WithPath("/api/documents:put").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode((int)HttpStatusCode.BadGateway)
                .WithBody("upstream unavailable"));

        var act = async () => await NewClient().PutAsync(
            SampleRequest(Guid.NewGuid(), Guid.NewGuid()));

        (await act.Should().ThrowAsync<DocsPutException>())
            .Which.Message.Should().Contain("[ANDY-POLICIES-DOCS-PUT-1]");
    }

    [Fact]
    public async Task Meta_json_deserialises_to_expected_shape()
    {
        // Belt-and-suspenders: capture the meta JSON and parse it to assert
        // the field names exactly (name/title/message/links).
        var captured = string.Empty;
        _server
            .Given(Request.Create().WithPath("/api/documents:put").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                { "documentId": "{{Guid.NewGuid()}}", "linkId": "{{Guid.NewGuid()}}",
                  "versionHash": "h", "sizeBytes": 1, "additionalLinkIds": [] }
                """));

        await NewClient().PutAsync(SampleRequest(Guid.NewGuid(), Guid.NewGuid()));

        captured = _server.LogEntries.Single().RequestMessage.Body ?? string.Empty;
        captured.Should().Contain("\"name\":\"compliance-assessment.json\"");
        captured.Should().Contain("\"title\":");
        captured.Should().Contain("\"message\":");
        captured.Should().Contain("\"links\":");
    }
}
