// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;

namespace Andy.Policies.Tests.Unit.Fixtures;

internal sealed class TestAuditWriter : IAuditWriter
{
    public static TestAuditWriter Instance { get; } = new();

    public Task AppendAsync(
        string action,
        Guid entityId,
        string actorSubjectId,
        string? rationale,
        CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class AllowAnyRationalePolicy : IRationalePolicy
{
    public static AllowAnyRationalePolicy Instance { get; } = new();

    public bool IsRequired => false;

    public string? ValidateRationale(string? rationale) => null;
}
