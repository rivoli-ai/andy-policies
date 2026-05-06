// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using Andy.Policies.Tests.Integration.Controllers;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Embedded;

/// <summary>
/// P10.2 (#35) backwards compatibility: Mode 1 (<c>dotnet run</c>)
/// and Mode 2 (<c>docker compose up</c>) leave
/// <c>ASPNETCORE_PATHBASE</c> empty. The pathbase block in
/// Program.cs no-ops in that case, and every route resolves at the
/// root. Pinned here against the standard <see cref="PoliciesApiFactory"/>
/// (which doesn't set the env var) so a future refactor that
/// makes pathbase mandatory would fail this test before merging.
/// </summary>
public class PathBaseUnsetTests : IClassFixture<PoliciesApiFactory>
{
    private readonly PoliciesApiFactory _factory;

    public PathBaseUnsetTests(PoliciesApiFactory factory)
    {
        // Defensive: ensure the env var leaked from PathBaseTests in
        // the same xUnit collection isn't sticking around. The two
        // class fixtures may run in interleaved orders depending on
        // xunit's default parallel scheduler.
        Environment.SetEnvironmentVariable("ASPNETCORE_PATHBASE", null);
        _factory = factory;
    }

    [Fact]
    public async Task Health_AtRoot_Returns200()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "Mode 1/2 boots without ASPNETCORE_PATHBASE; routes must remain at " +
            "the root, otherwise existing dotnet-run / docker-compose deployments " +
            "regress on the day P10.2 ships.");
    }

    [Fact]
    public async Task ApiPolicies_AtRoot_Returns200()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/policies");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
