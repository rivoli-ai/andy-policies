// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Data;

/// <summary>
/// Boot-time seeder for the default agent → policy bindings (SD4.2,
/// rivoli-ai/andy-policies#1182). Reads <c>config/bindings-seed.json</c>
/// and creates a <see cref="Binding"/> row per (agent, policy) pair
/// targeting the v1 Active version of the named policy. Idempotent across
/// reruns: de-duped by
/// <c>(PolicyVersionId, TargetType=Agent, TargetRef='agent:{slug}')</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate seeder?</b> <see cref="PolicySeeder"/> owns the
/// catalog; this seeder owns the consumer attachment graph. Splitting
/// them keeps the policy-version writes (which interact with the
/// immutable-after-Active guard rails) cleanly separated from the
/// binding rows that pin agents to those versions.
/// </para>
/// <para>
/// <b>Agent slug validation.</b> Per the SD4.2 acceptance criteria, every
/// binding's <see cref="Binding.TargetRef"/> must reference one of the
/// six known agent slugs (<c>triage</c>, <c>research</c>, <c>planning</c>,
/// <c>coding</c>, <c>validation</c>, <c>review</c>). Validation is
/// against the <c>agents</c> array in the seed JSON itself — andy-policies
/// never calls andy-agents at seed time.
/// </para>
/// <para>
/// <b>Bundle-snapshot interaction.</b> Bindings do not auto-bundle. A
/// bundle is created on demand via <c>BundleService.CreateAsync</c> and
/// is an immutable snapshot of the catalog; this seeder writes only
/// <see cref="Binding"/> rows, never <see cref="Bundle"/> rows, so
/// reseeding does not bump any bundle.
/// </para>
/// <para>
/// <b>Audit chain.</b> Boot-time bindings are not audited. Production
/// binding mutations go through <c>BindingService.CreateAsync</c> which
/// appends a <c>binding.created</c> audit event; the seed path
/// deliberately bypasses the audit append for the same reason the
/// policy seeder writes Active rows directly — the seeded baseline is
/// pre-existence, not a user-initiated mutation.
/// </para>
/// </remarks>
public static class BindingSeeder
{
    /// <summary>Default location of the seed JSON, relative to the content root.</summary>
    public const string SeedConfigRelativePath = "config/bindings-seed.json";

    /// <summary>Canonical TargetRef prefix for <see cref="BindingTargetType.Agent"/>.</summary>
    public const string AgentTargetRefPrefix = "agent:";

    /// <summary>
    /// Canonical projection of <c>config/bindings-seed.json</c> embedded
    /// in code so unit tests can assert the binding edges without re-
    /// reading the file. Order matches the file row-by-row.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing for downstream consumers that diff the
    /// seeded set across versions; a drive-by reorder triggers the
    /// fixture-parity test in
    /// <c>BindingSeederTests.SeedConfigJson_Matches_EmbeddedFixture</c>.
    /// </remarks>
    public static readonly IReadOnlyList<string> SeedAgentSlugs = new[]
    {
        "triage",
        "research",
        "planning",
        "coding",
        "validation",
        "review",
    };

    /// <summary>Embedded mirror of <c>config/bindings-seed.json</c>'s <c>bindings</c> array.</summary>
    public static readonly IReadOnlyList<SeedBinding> SeedBindings = new[]
    {
        new SeedBinding("triage",     "read-only",    BindStrength.Mandatory),
        new SeedBinding("research",   "read-only",    BindStrength.Mandatory),
        new SeedBinding("review",     "read-only",    BindStrength.Mandatory),

        new SeedBinding("planning",   "draft-only",   BindStrength.Mandatory),

        new SeedBinding("coding",     "write-branch", BindStrength.Mandatory),
        new SeedBinding("coding",     "sandboxed",    BindStrength.Mandatory),

        new SeedBinding("validation", "sandboxed",    BindStrength.Mandatory),
        new SeedBinding("validation", "no-prod",      BindStrength.Mandatory),

        new SeedBinding("triage",     "no-prod",      BindStrength.Mandatory),
        new SeedBinding("research",   "no-prod",      BindStrength.Mandatory),
        new SeedBinding("planning",   "no-prod",      BindStrength.Mandatory),
        new SeedBinding("coding",     "no-prod",      BindStrength.Mandatory),
        new SeedBinding("review",     "no-prod",      BindStrength.Mandatory),

        new SeedBinding("triage",     "high-risk",    BindStrength.Mandatory),
        new SeedBinding("research",   "high-risk",    BindStrength.Mandatory),
        new SeedBinding("planning",   "high-risk",    BindStrength.Mandatory),
        new SeedBinding("coding",     "high-risk",    BindStrength.Mandatory),
        new SeedBinding("validation", "high-risk",    BindStrength.Mandatory),
        new SeedBinding("review",     "high-risk",    BindStrength.Mandatory),
    };

    /// <summary>
    /// Seeds the default agent → policy bindings (SD4.2 #1182).
    /// Per-row idempotent: each (PolicyVersionId, Agent slug) pair is
    /// inserted at most once. Re-runs on a populated catalog are a no-op.
    /// </summary>
    /// <param name="db">DbContext to seed against.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task SeedDefaultBindingsAsync(AppDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Resolve policy slug → active PolicyVersion.Id once. Missing policy
        // rows are skipped (the corresponding row will land on the next
        // boot, after PolicySeeder runs); missing Active versions also skip
        // silently — there's nothing to bind to. This keeps the boot path
        // crash-free in a partially-seeded environment.
        var versionsBySlug = await db.Policies
            .AsNoTracking()
            .Join(
                db.PolicyVersions.AsNoTracking().Where(v => v.State == LifecycleState.Active),
                p => p.Id,
                v => v.PolicyId,
                (p, v) => new { p.Name, VersionId = v.Id })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var versionLookup = versionsBySlug.ToDictionary(
            x => x.Name, x => x.VersionId, StringComparer.Ordinal);

        // Pull live agent-target bindings once. We dedupe in-memory rather
        // than per-row probing the DB: the set is bounded (~20 rows for
        // the SD4 seed; capped at policies×agents = 36 even with future
        // growth) and the index ix_bindings_target makes the round-trip
        // cheap. Soft-deleted rows count as present so an operator who
        // intentionally deleted a binding is not silently re-added on
        // every boot — the seeder is "seed once, not enforce forever".
        var existingPairs = await db.Bindings
            .AsNoTracking()
            .Where(b => b.TargetType == BindingTargetType.Agent)
            .Select(b => new { b.PolicyVersionId, b.TargetRef })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingSet = new HashSet<(Guid VersionId, string TargetRef)>(
            existingPairs.Select(p => (p.PolicyVersionId, p.TargetRef)));

        var now = DateTimeOffset.UtcNow;
        var added = 0;
        foreach (var seed in SeedBindings)
        {
            if (!versionLookup.TryGetValue(seed.PolicySlug, out var versionId))
            {
                // Policy not seeded (or not yet Active). Skip — the next
                // boot, after PolicySeeder lands the row, will pick it up.
                continue;
            }
            var targetRef = AgentTargetRefPrefix + seed.AgentSlug;
            if (existingSet.Contains((versionId, targetRef)))
            {
                continue;
            }

            db.Bindings.Add(new Binding
            {
                Id = Guid.NewGuid(),
                PolicyVersionId = versionId,
                TargetType = BindingTargetType.Agent,
                TargetRef = targetRef,
                BindStrength = seed.BindStrength,
                CreatedAt = now,
                CreatedBySubjectId = PolicySeeder.SeedSubjectId,
            });
            existingSet.Add((versionId, targetRef));
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses <c>config/bindings-seed.json</c>. Mirrors
    /// <see cref="PolicySeeder.LoadSeedConfig"/>; called by manifest-
    /// parity tests so the file and the in-code mirror stay aligned.
    /// </summary>
    public static SeedConfig LoadSeedConfig(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        var doc = JsonSerializer.Deserialize<SeedConfig>(stream, PolicySeeder.SeedConfigJsonOptions)
            ?? throw new InvalidOperationException(
                $"bindings-seed JSON at '{path}' parsed to null.");
        return doc;
    }

    /// <summary>One row in <see cref="SeedBindings"/>: agent slug → policy slug edge.</summary>
    public sealed record SeedBinding(string AgentSlug, string PolicySlug, BindStrength BindStrength);

    /// <summary>Top-level shape of <c>config/bindings-seed.json</c>.</summary>
    public sealed class SeedConfig
    {
        public List<string> Agents { get; set; } = new();
        public List<SeedBindingRow> Bindings { get; set; } = new();
    }

    /// <summary>One binding row inside <see cref="SeedConfig.Bindings"/>.</summary>
    public sealed class SeedBindingRow
    {
        public string Agent { get; set; } = string.Empty;
        public string Policy { get; set; } = string.Empty;
        public string BindStrength { get; set; } = "Mandatory";
    }
}
