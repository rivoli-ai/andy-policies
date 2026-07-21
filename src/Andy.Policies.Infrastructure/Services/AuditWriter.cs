// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using System.Text.Json;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Compatibility adapter from the early mutation hook to the production
/// tamper-evident audit chain. The caller owns the transaction; because
/// both services share the scoped AppDbContext, the audit append joins it.
/// </summary>
public sealed class AuditWriter : IAuditWriter
{
    private readonly IAuditChain _chain;

    public AuditWriter(IAuditChain chain)
    {
        _chain = chain;
    }

    public Task AppendAsync(
        string action,
        Guid entityId,
        string actorSubjectId,
        string? rationale,
        CancellationToken ct = default)
    {
        var prefix = action.Split('.', 2)[0];
        var entityType = prefix switch
        {
            "policy" => "PolicyVersion",
            "binding" => "Binding",
            "scope" => "ScopeNode",
            "override" => "Override",
            "item" => "Item",
            _ => "CatalogEntity",
        };
        return AppendCoreAsync(action, entityType, entityId, actorSubjectId, rationale, ct);
    }

    private async Task AppendCoreAsync(
        string action,
        string entityType,
        Guid entityId,
        string actorSubjectId,
        string? rationale,
        CancellationToken ct)
    {
        await _chain.AppendAsync(new AuditAppendRequest(
            Action: action,
            EntityType: entityType,
            EntityId: entityId.ToString(),
            // The compatibility hook does not receive before/after entity
            // snapshots, but mutation audit rows must still carry a
            // populated JSON Patch document. Record the stable mutation
            // action instead of emitting an ambiguous empty patch.
            FieldDiffJson: JsonSerializer.Serialize(new[]
            {
                new { op = "replace", path = "/lastMutation", value = action },
            }),
            Rationale: string.IsNullOrWhiteSpace(rationale) ? null : rationale.Trim(),
            ActorSubjectId: actorSubjectId,
            ActorRoles: Array.Empty<string>()), ct).ConfigureAwait(false);
    }
}
