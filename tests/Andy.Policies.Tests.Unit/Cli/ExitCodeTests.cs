// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using Andy.Policies.Cli.Output;
using Xunit;

namespace Andy.Policies.Tests.Unit.Cli;

/// <summary>
/// P1.8 acceptance: HTTP status codes map onto the federated-CLI exit-code
/// contract from Epic AN (rivoli-ai/conductor#753). 401/403 → 3 (auth),
/// 404 → 4 (not-found), 409/412 → 5 (conflict), 5xx and others → 1 (transport).
/// </summary>
public class ExitCodeTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, ExitCodes.Success)]
    [InlineData(HttpStatusCode.Created, ExitCodes.Success)]
    [InlineData(HttpStatusCode.NoContent, ExitCodes.Success)]
    [InlineData(HttpStatusCode.Unauthorized, ExitCodes.Auth)]
    [InlineData(HttpStatusCode.Forbidden, ExitCodes.Auth)]
    [InlineData(HttpStatusCode.NotFound, ExitCodes.NotFound)]
    [InlineData(HttpStatusCode.Conflict, ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed, ExitCodes.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError, ExitCodes.Transport)]
    [InlineData(HttpStatusCode.BadGateway, ExitCodes.Transport)]
    public void FromStatus_MapsHttpToExitCode(HttpStatusCode status, int expected)
    {
        Assert.Equal(expected, ExitCodes.FromStatus(status));
    }
}
