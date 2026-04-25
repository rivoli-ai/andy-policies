// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;

namespace Andy.Policies.Cli.Output;

/// <summary>
/// Maps HTTP responses to the federated-CLI exit-code contract from Epic AN
/// (rivoli-ai/conductor#753): 0 success, 1 transport/unexpected, 2 bad arguments
/// (handled by System.CommandLine), 3 auth, 4 not found, 5 conflict.
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Transport = 1;
    public const int BadArguments = 2;
    public const int Auth = 3;
    public const int NotFound = 4;
    public const int Conflict = 5;

    public static int FromStatus(HttpStatusCode status) => status switch
    {
        var s when (int)s >= 200 && (int)s < 300 => Success,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Auth,
        HttpStatusCode.NotFound => NotFound,
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => Conflict,
        _ => Transport,
    };

    public static async Task<int> HandleAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        if (response.IsSuccessStatusCode)
        {
            return Success;
        }

        var code = FromStatus(response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var message = code switch
        {
            Auth => "Authentication failed. Check --token.",
            NotFound => "Not found.",
            Conflict => string.IsNullOrWhiteSpace(body) ? "Conflict." : body.Trim(),
            _ => $"Request failed: {(int)response.StatusCode} {response.ReasonPhrase}".Trim(),
        };

        if (code != Conflict && !string.IsNullOrWhiteSpace(body))
        {
            await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(body.Trim()).ConfigureAwait(false);
        }
        else
        {
            await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
        }
        return code;
    }
}
