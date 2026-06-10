// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Data;

/// <summary>
/// Boot-time seeder for the AX development-standards catalog — 101
/// professional software-development standards (Epic AX,
/// rivoli-ai/conductor#2087) extracted from the dev-standards corpus.
/// </summary>
/// <remarks>
/// <para>
/// These standards are a <b>separate catalog</b> from the six capability
/// guardrails in <see cref="PolicySeeder"/>: guardrails govern what an agent
/// <i>may do</i> (read-only, write-branch, no-prod, ...); standards govern
/// <i>how work is done</i> (acceptance criteria, error handling, test
/// layering, ...). They coexist in the same `Policies` table and are
/// distinguishable by the `std-` slug prefix and their scope tokens
/// (`area:`/`phase:`/`lang:`/`domain:`/`topic:`), which let consumers
/// (andy-tasks planner) narrow standards per task.
/// </para>
/// <para>
/// Source of truth is <c>config/standards-seed.json</c>, embedded into this
/// assembly (mirroring andy-agents' built-in agent manifest) so the seed
/// never depends on a config file shipping alongside the binary.
/// </para>
/// <para>
/// <b>Idempotency.</b> Identical to <see cref="PolicySeeder"/>: per-row by
/// slug. An existing <see cref="Policy"/> with the same name short-circuits
/// the insert for that row; operator-edited rows are never updated. Rows
/// land as a single v1 in <see cref="LifecycleState.Active"/> with the same
/// stamped fields <c>LifecycleTransitionService.Publish</c> would write.
/// </para>
/// </remarks>
public static class StandardsSeeder
{
    /// <summary>Embedded-resource name of the standards seed JSON.</summary>
    public const string ResourceName = "Andy.Policies.Infrastructure.Data.standards-seed.json";

    /// <summary>
    /// Seeds the standards catalog. Per-row idempotent by slug; rows land
    /// as Active v1. Never updates existing rows.
    /// </summary>
    public static async Task SeedStandardsAsync(AppDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var config = LoadEmbeddedSeedConfig();

        var existing = await db.Policies
            .AsNoTracking()
            .Select(p => p.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);

        var toAdd = config.Policies.Where(s => !existingSet.Contains(s.Name)).ToList();
        if (toAdd.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var standard in toAdd)
        {
            var policy = new Policy
            {
                Id = Guid.NewGuid(),
                Name = standard.Name,
                Description = standard.Description,
                CreatedAt = now,
                CreatedBySubjectId = PolicySeeder.SeedSubjectId,
            };
            var version = new PolicyVersion
            {
                Id = Guid.NewGuid(),
                PolicyId = policy.Id,
                Version = 1,
                State = LifecycleState.Active,
                Enforcement = ParseEnforcement(standard),
                Severity = ParseSeverity(standard),
                Scopes = standard.Scopes.ToList(),
                Summary = standard.Description,
                RulesJson = standard.RulesJson.GetRawText(),
                CreatedAt = now,
                CreatedBySubjectId = PolicySeeder.SeedSubjectId,
                ProposerSubjectId = PolicySeeder.SeedSubjectId,
                PublishedAt = now,
                PublishedBySubjectId = PolicySeeder.SeedSubjectId,
            };
            db.Policies.Add(policy);
            db.PolicyVersions.Add(version);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the embedded standards seed and validates its invariants
    /// (non-empty, unique `std-` slugs, parseable enums, non-empty rules).
    /// Public so the manifest-parity unit tests can assert against the
    /// exact corpus the production seeder uses.
    /// </summary>
    public static PolicySeeder.SeedConfig LoadEmbeddedSeedConfig()
    {
        var asm = typeof(StandardsSeeder).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found. Available: " +
                string.Join(", ", asm.GetManifestResourceNames()));

        var config = JsonSerializer.Deserialize<PolicySeeder.SeedConfig>(
                stream, PolicySeeder.SeedConfigJsonOptions)
            ?? throw new InvalidOperationException("standards-seed JSON deserialized to null.");

        if (config.Policies.Count == 0)
        {
            throw new InvalidOperationException("standards-seed JSON contains no policies.");
        }

        var dupes = config.Policies
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (dupes.Count > 0)
        {
            throw new InvalidOperationException(
                $"standards-seed has duplicate slugs: {string.Join(", ", dupes)}");
        }

        var badSlugs = config.Policies
            .Where(p => !p.Name.StartsWith("std-", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToList();
        if (badSlugs.Count > 0)
        {
            throw new InvalidOperationException(
                $"standards-seed slugs must carry the std- prefix; offending: {string.Join(", ", badSlugs)}");
        }

        // Fail at load time (not mid-seed) on an unparseable enum so a
        // corpus regeneration with a new severity word breaks loudly.
        foreach (var p in config.Policies)
        {
            _ = ParseEnforcement(p);
            _ = ParseSeverity(p);
        }

        return config;
    }

    private static EnforcementLevel ParseEnforcement(PolicySeeder.SeedPolicy p) =>
        Enum.TryParse<EnforcementLevel>(p.Enforcement, ignoreCase: true, out var level)
            ? level
            : throw new InvalidOperationException(
                $"standards-seed '{p.Name}': unknown enforcement '{p.Enforcement}'.");

    private static Severity ParseSeverity(PolicySeeder.SeedPolicy p) =>
        Enum.TryParse<Severity>(p.Severity, ignoreCase: true, out var severity)
            ? severity
            : throw new InvalidOperationException(
                $"standards-seed '{p.Name}': unknown severity '{p.Severity}'.");
}
