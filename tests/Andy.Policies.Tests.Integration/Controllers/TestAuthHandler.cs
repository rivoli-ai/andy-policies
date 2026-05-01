// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// Always-succeeds authentication handler for integration tests. Issues a fake
/// principal named <c>test-user</c> so <c>[Authorize]</c> attributes pass.
/// Production auth (JWT Bearer via Andy Auth) is replaced wholesale by the
/// <see cref="PoliciesApiFactory"/> — this scheme exists only inside the factory.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string TestSubjectId = "test-user";

    /// <summary>
    /// Optional request header that overrides <see cref="TestSubjectId"/>
    /// for a single request. P5.5 (#58) needs multi-actor scenarios
    /// (self-approval, propose-as-A + approve-as-B); the header lets
    /// tests pin the subject without spinning up a second factory.
    /// </summary>
    public const string SubjectHeader = "X-Test-Subject";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subjectId = Request.Headers.TryGetValue(SubjectHeader, out var override_) && !string.IsNullOrEmpty(override_)
            ? override_.ToString()
            : TestSubjectId;
        var claims = new[] { new Claim(ClaimTypes.Name, subjectId) };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
