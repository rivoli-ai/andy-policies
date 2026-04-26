// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Data;

/// <summary>
/// Boot-time seeder for the six canonical stock policies (P1.3, #73). Each policy
/// lands with a single <see cref="LifecycleState.Draft"/> v1; promotion to
/// <see cref="LifecycleState.Active"/> is operator-driven via Epic P2 and is
/// intentionally out of scope here.
/// </summary>
/// <remarks>
/// Idempotency is by-presence: if the catalog has any rows we short-circuit. That
/// preserves operator edits across restarts and means the seeder is safe to run
/// from <c>Program.cs</c> on every boot. A re-seed escape hatch (CLI flag, bundle
/// import) is the responsibility of P1.8 / Epic P8 and not this story.
/// </remarks>
public static class PolicySeeder
{
    /// <summary>Subject id stamped on seed-created rows. Filterable in audit queries.</summary>
    public const string SeedSubjectId = "system:seed";

    /// <summary>
    /// Six stock policies sourced from the andy-rbac#18 reconciliation note (Epic V V2).
    /// Public so unit tests can assert the table row-by-row without re-listing the values.
    /// </summary>
    public static readonly IReadOnlyList<StockPolicy> StockPolicies = new[]
    {
        new StockPolicy(
            Name: "read-only",
            Enforcement: EnforcementLevel.Must,
            Severity: Severity.Info,
            Scopes: Array.Empty<string>(),
            Summary: "Read/list operations only; no writes, no side effects."),
        new StockPolicy(
            Name: "write-branch",
            Enforcement: EnforcementLevel.Should,
            Severity: Severity.Moderate,
            Scopes: new[] { "repo" },
            Summary: "Writes are permitted only on non-default branches."),
        new StockPolicy(
            Name: "sandboxed",
            Enforcement: EnforcementLevel.Must,
            Severity: Severity.Moderate,
            Scopes: new[] { "tool", "container" },
            Summary: "Execution must occur inside an isolated container/sandbox."),
        new StockPolicy(
            Name: "draft-only",
            Enforcement: EnforcementLevel.Must,
            Severity: Severity.Info,
            Scopes: new[] { "template" },
            Summary: "Output is advisory/draft; no publish/merge/deploy."),
        new StockPolicy(
            Name: "no-prod",
            Enforcement: EnforcementLevel.Must,
            Severity: Severity.Critical,
            Scopes: new[] { "prod" },
            Summary: "No actions against production resources."),
        new StockPolicy(
            Name: "high-risk",
            Enforcement: EnforcementLevel.Must,
            Severity: Severity.Critical,
            Scopes: Array.Empty<string>(),
            Summary: "Requires explicit approver sign-off."),
    };

    public static async Task SeedStockPoliciesAsync(AppDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (await db.Policies.AnyAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var stock in StockPolicies)
        {
            var policy = new Policy
            {
                Id = Guid.NewGuid(),
                Name = stock.Name,
                Description = stock.Summary,
                CreatedAt = now,
                CreatedBySubjectId = SeedSubjectId,
            };
            var version = new PolicyVersion
            {
                Id = Guid.NewGuid(),
                PolicyId = policy.Id,
                Version = 1,
                State = LifecycleState.Draft,
                Enforcement = stock.Enforcement,
                Severity = stock.Severity,
                Scopes = stock.Scopes.ToList(),
                Summary = stock.Summary,
                RulesJson = "{}",
                CreatedAt = now,
                CreatedBySubjectId = SeedSubjectId,
                ProposerSubjectId = SeedSubjectId,
            };
            db.Policies.Add(policy);
            db.PolicyVersions.Add(version);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public sealed record StockPolicy(
        string Name,
        EnforcementLevel Enforcement,
        Severity Severity,
        IReadOnlyCollection<string> Scopes,
        string Summary);
}
