// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;

namespace Andy.Policies.Infrastructure.Services
{
    internal sealed class IntegrationTestAuditWriter : Application.Interfaces.IAuditWriter
    {
        public static IntegrationTestAuditWriter Instance { get; } = new();

        public Task AppendAsync(
            string action,
            Guid entityId,
            string actorSubjectId,
            string? rationale,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class IntegrationAllowAnyRationalePolicy : Application.Interfaces.IRationalePolicy
    {
        public static IntegrationAllowAnyRationalePolicy Instance { get; } = new();

        public bool IsRequired => false;

        public string? ValidateRationale(string? rationale) => null;
    }
}

namespace Andy.Policies.Application.Interfaces
{
    internal static class ScopeServiceIntegrationTestExtensions
    {
        private const string Actor = "test:actor";

        public static Task<ScopeNodeDto> CreateAsync(
            this IScopeService service,
            CreateScopeNodeRequest request,
            CancellationToken ct = default) => service.CreateAsync(request, Actor, ct);

        public static Task<ScopeNodeDto> UpdateAsync(
            this IScopeService service,
            Guid id,
            UpdateScopeNodeRequest request,
            CancellationToken ct = default) => service.UpdateAsync(id, request, Actor, ct);

        public static Task DeleteAsync(
            this IScopeService service,
            Guid id,
            CancellationToken ct = default) =>
            service.DeleteAsync(id, Actor, "test rationale", ct);
    }
}
