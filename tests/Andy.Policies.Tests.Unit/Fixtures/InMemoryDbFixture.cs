// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Andy.Policies.Tests.Unit.Fixtures;

/// <summary>
/// Single source of truth for spinning up a fresh <see cref="AppDbContext"/>
/// over EF Core InMemory in unit tests (P1.10, #80). Each call yields a context
/// over a distinct in-memory database so xUnit collection-level parallelism
/// can run safely. The
/// <see cref="InMemoryEventId.TransactionIgnoredWarning"/> is suppressed
/// because <see cref="Andy.Policies.Infrastructure.Services.PolicyService"/>
/// opens explicit transactions on Create/Bump that the InMemory provider
/// cannot honour — the logical test semantics are unchanged.
/// </summary>
internal static class InMemoryDbFixture
{
    public static AppDbContext Create(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }
}
