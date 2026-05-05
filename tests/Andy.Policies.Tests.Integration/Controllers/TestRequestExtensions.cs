// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// HTTP client helpers for integration tests that need to issue
/// requests as a specific subject. P7.3 (#55) introduced the
/// "author cannot self-approve" invariant: tests that previously
/// created and published with a single client now need to swap
/// subjects on the publish call. The simplest readable mechanism
/// is the <c>X-Test-Subject</c> header that <see cref="TestAuthHandler"/>
/// already honours per-request.
/// </summary>
internal static class TestRequestExtensions
{
    /// <summary>POST a JSON body as <see cref="TestAuthHandler.TestApproverSubjectId"/>.</summary>
    public static Task<HttpResponseMessage> PostAsJsonAsApproverAsync<T>(
        this HttpClient client, string url, T body, CancellationToken ct = default)
        => client.SendAsync(BuildPostJson(url, body, TestAuthHandler.TestApproverSubjectId), ct);

    /// <summary>POST a JSON body as the named subject.</summary>
    public static Task<HttpResponseMessage> PostAsJsonAsSubjectAsync<T>(
        this HttpClient client, string url, T body, string subjectId, CancellationToken ct = default)
        => client.SendAsync(BuildPostJson(url, body, subjectId), ct);

    /// <summary>POST with no body as the named subject.</summary>
    public static Task<HttpResponseMessage> PostEmptyAsSubjectAsync(
        this HttpClient client, string url, string subjectId, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add(TestAuthHandler.SubjectHeader, subjectId);
        return client.SendAsync(req, ct);
    }

    private static HttpRequestMessage BuildPostJson<T>(string url, T body, string subjectId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add(TestAuthHandler.SubjectHeader, subjectId);
        return req;
    }
}
