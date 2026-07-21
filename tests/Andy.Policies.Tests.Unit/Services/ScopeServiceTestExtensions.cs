// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Infrastructure.Services;

namespace Andy.Policies.Tests.Unit.Services;

internal static class ScopeServiceTestExtensions
{
    private const string Actor = "test:actor";

    public static Task<ScopeNodeDto> CreateAsync(
        this ScopeService service,
        CreateScopeNodeRequest request,
        CancellationToken ct = default) =>
        service.CreateAsync(request, Actor, ct);

    public static Task<ScopeNodeDto> UpdateAsync(
        this ScopeService service,
        Guid id,
        UpdateScopeNodeRequest request,
        CancellationToken ct = default) =>
        service.UpdateAsync(id, request, Actor, ct);

    public static Task DeleteAsync(
        this ScopeService service,
        Guid id,
        CancellationToken ct = default) =>
        service.DeleteAsync(id, Actor, "test rationale", ct);
}
