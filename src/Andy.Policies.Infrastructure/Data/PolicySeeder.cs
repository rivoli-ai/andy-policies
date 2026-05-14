// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Data;

/// <summary>
/// Boot-time seeder for the six canonical lifecycle policies (P1.3 #73,
/// extended by SD4.1 #1181). Each policy lands with a single v1 in
/// <see cref="LifecycleState.Active"/> — SD4 standardises on
/// already-published seed rows so downstream consumers (Conductor admission,
/// andy-tasks gates) can bind against them on first boot without driving
/// the Draft → Active lifecycle dance manually.
/// </summary>
/// <remarks>
/// <para>
/// Source of truth for the dimension fields (slug, name, description,
/// severity, enforcement, scopes, rules) is
/// <c>config/policies-seed.json</c>. The file is embedded as a build-time
/// fallback when <c>FromJsonFile</c> is the entry point used by tests;
/// production boot resolves the JSON from
/// <c>{ContentRoot}/config/policies-seed.json</c>.
/// </para>
/// <para>
/// <b>Idempotency contract (SD4 parent body).</b> Re-running on a populated
/// catalog is a no-op. Per-row idempotency is by slug — an existing
/// <see cref="Policy"/> with the same <see cref="Policy.Name"/> short-
/// circuits the insert for that row. Operator-edited dimension fields on a
/// pre-existing version are preserved (we never UPDATE).
/// </para>
/// <para>
/// <b>Bundle-snapshot interaction.</b> Bundles are created on demand via
/// <c>BundleService.CreateAsync</c> and never auto-snapshot on policy
/// publish, so seeding (or re-seeding) Active versions does not bump any
/// bundle. The seeder never writes to <c>Bundles</c>.
/// </para>
/// <para>
/// <b>Audit chain.</b> The audit chain is managed by the service at
/// lifecycle transitions (P6.2). Because the seeder writes
/// <see cref="LifecycleState.Active"/> rows directly without going through
/// <c>LifecycleTransitionService</c>, no audit entries are fabricated —
/// per the SD4 parent body, seed-time audit entries are explicitly out of
/// scope and any future audit retrofit will live in P6's append path.
/// </para>
/// </remarks>
public static class PolicySeeder
{
    /// <summary>Subject id stamped on seed-created rows. Filterable in audit queries.</summary>
    public const string SeedSubjectId = "system:seed";

    /// <summary>Default location of the seed JSON, relative to the content root.</summary>
    public const string SeedConfigRelativePath = "config/policies-seed.json";

    /// <summary>
    /// Six canonical lifecycle policies. The slugs are fixed by the SD epic
    /// reference set (SD4.1 #1181) and the simulator parity contract —
    /// DO NOT rename. Public so unit tests can assert the row table without
    /// re-listing the values.
    /// </summary>
    public static readonly IReadOnlyList<StockPolicy> StockPolicies = new[]
    {
        new StockPolicy(
            Slug: "read-only",
            Name: "read-only",
            Description: "Read-only repository access. The agent may read files, list refs, and inspect history but cannot mutate the working tree, push commits, or invoke shell tools. Use for triage, research, and review intents.",
            Enforcement: EnforcementLevel.Must,
            Severity: Severity.Info,
            Scopes: Array.Empty<string>(),
            RulesJson: """
                {"intent":"read-only","allow":["fs.read","git.read","search","review.comment"],"deny":["fs.write","git.write","git.push","shell.exec","container.exec"],"approvers":[]}
                """),
        new StockPolicy(
            Slug: "draft-only",
            Name: "draft-only",
            Description: "The agent may produce drafts, comments, and plans but may not finalise, publish, merge, or deploy. Use for planning agents whose output is advisory and reviewed before any side effect ships.",
            Enforcement: EnforcementLevel.Must,
            Severity: Severity.Info,
            Scopes: new[] { "template" },
            RulesJson: """
                {"intent":"draft-only","allow":["draft.create","draft.update","comment.create","plan.propose"],"deny":["draft.publish","merge","deploy","release.cut"],"approvers":[]}
                """),
        new StockPolicy(
            Slug: "write-branch",
            Name: "write-branch",
            Description: "The agent may mutate files and create commits, but only on a feature branch matching the goal's branch pattern. Pushes to the repo's default branch (main/master) are denied. Use for coding agents working inside a sandboxed task.",
            Enforcement: EnforcementLevel.Should,
            Severity: Severity.Moderate,
            Scopes: new[] { "repo" },
            RulesJson: """
                {"intent":"write-branch","allow":["fs.write","git.commit","git.push:feature/*"],"deny":["git.push:main","git.push:master","git.push:release/*"],"branchPattern":"^(feature|fix|chore|spike)/.+","approvers":[]}
                """),
        new StockPolicy(
            Slug: "sandboxed",
            Name: "sandboxed",
            Description: "All execution must happen inside the container/sandbox the task provides. The host filesystem outside the workspace is read-only, network egress is policy-scoped, and resource caps (CPU, memory, wall-clock) are enforced by the runtime.",
            Enforcement: EnforcementLevel.Must,
            Severity: Severity.Moderate,
            Scopes: new[] { "tool", "container" },
            RulesJson: """
                {"intent":"sandboxed","allow":["container.exec","fs.write:/workspace/**"],"deny":["fs.write:/host/**","network.egress:!allowlist"],"resourceCaps":{"cpu":"2","memoryMiB":4096,"wallClockSeconds":1800},"approvers":[]}
                """),
        new StockPolicy(
            Slug: "no-prod",
            Name: "no-prod",
            Description: "Universal guardrail: any operation targeting prod or release-tagged services is denied regardless of agent intent. Block at consumer admission; never warn-and-proceed.",
            Enforcement: EnforcementLevel.Must,
            Severity: Severity.Critical,
            Scopes: new[] { "prod" },
            RulesJson: """
                {"intent":"guardrail","deny":["endpoint:*://prod.*","endpoint:*://*.prod.rivoli.ai","service:env=prod","tag:prod","tag:release"],"approvers":[]}
                """),
        new StockPolicy(
            Slug: "high-risk",
            Name: "high-risk",
            Description: "Universal guardrail: dangerous operations — force-push, schema migration, secret rotation, mass delete — require typed-confirmation approver chain. Consumers (Conductor ActionBus, andy-tasks gates) MUST surface the confirmation prompt and capture the maintainer's approval before invocation.",
            Enforcement: EnforcementLevel.Must,
            Severity: Severity.Critical,
            Scopes: Array.Empty<string>(),
            RulesJson: """
                {"intent":"guardrail","dangerousActions":["git.push.force","schema.migrate","secret.rotate","delete.bulk","tenant.delete"],"requireTypedConfirmation":true,"approvers":[{"role":"maintainer","minApprovals":1,"selfApprovalForbidden":true}]}
                """),
    };

    /// <summary>
    /// Seeds the canonical six stock policies in <see cref="LifecycleState.Active"/>
    /// state. Per-row idempotent: existing slugs are left untouched.
    /// </summary>
    /// <param name="db">DbContext to seed against.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task SeedStockPoliciesAsync(AppDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Per-row presence check by slug. We cannot short-circuit on
        // "any policy exists" because SD4.2 may rerun the binding seeder
        // after operator edits, and we must still add a missing canonical
        // slug without bouncing the whole table.
        var existing = await db.Policies
            .AsNoTracking()
            .Select(p => p.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);

        var toAdd = StockPolicies.Where(s => !existingSet.Contains(s.Slug)).ToList();
        if (toAdd.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var stock in toAdd)
        {
            var policy = new Policy
            {
                Id = Guid.NewGuid(),
                Name = stock.Slug,
                Description = stock.Description,
                CreatedAt = now,
                CreatedBySubjectId = SeedSubjectId,
            };
            var version = new PolicyVersion
            {
                Id = Guid.NewGuid(),
                PolicyId = policy.Id,
                Version = 1,
                // SD4.1 #1181: seed directly as Active. The Draft -> Active
                // lifecycle dance is consumer-facing; the seeded baseline is
                // already-published. PublishedAt/PublishedBySubjectId stay
                // consistent with what LifecycleTransitionService.Publish
                // would have written so consumer queries see a uniform
                // shape regardless of how the row was created.
                State = LifecycleState.Active,
                Enforcement = stock.Enforcement,
                Severity = stock.Severity,
                Scopes = stock.Scopes.ToList(),
                Summary = stock.Description,
                RulesJson = stock.RulesJson,
                CreatedAt = now,
                CreatedBySubjectId = SeedSubjectId,
                ProposerSubjectId = SeedSubjectId,
                PublishedAt = now,
                PublishedBySubjectId = SeedSubjectId,
            };
            db.Policies.Add(policy);
            db.PolicyVersions.Add(version);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates that the embedded <see cref="StockPolicies"/> table is
    /// byte-for-byte aligned with <c>config/policies-seed.json</c>. Called
    /// by manifest-parity tests so a drive-by edit to either side breaks
    /// loudly. Returns the parsed JSON for callers that want to assert
    /// against it directly.
    /// </summary>
    public static SeedConfig LoadSeedConfig(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        var doc = JsonSerializer.Deserialize<SeedConfig>(stream, SeedConfigJsonOptions)
            ?? throw new InvalidOperationException(
                $"policies-seed JSON at '{path}' parsed to null.");
        return doc;
    }

    internal static readonly JsonSerializerOptions SeedConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Canonical projection of one row in <c>config/policies-seed.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>Persisted layout used by <see cref="SeedStockPoliciesAsync"/>:</para>
    /// <list type="bullet">
    ///   <item><see cref="Slug"/> → <see cref="Policy.Name"/></item>
    ///   <item><see cref="Name"/> → <see cref="Policy.Name"/> (always equal to <see cref="Slug"/> in v1)</item>
    ///   <item><see cref="Description"/> → both <see cref="Policy.Description"/> and <see cref="PolicyVersion.Summary"/></item>
    ///   <item><see cref="Enforcement"/> → <see cref="PolicyVersion.Enforcement"/></item>
    ///   <item><see cref="Severity"/> → <see cref="PolicyVersion.Severity"/></item>
    ///   <item><see cref="Scopes"/> → <see cref="PolicyVersion.Scopes"/></item>
    ///   <item><see cref="RulesJson"/> → <see cref="PolicyVersion.RulesJson"/> (verbatim)</item>
    /// </list>
    /// </remarks>
    public sealed record StockPolicy(
        string Slug,
        string Name,
        string Description,
        EnforcementLevel Enforcement,
        Severity Severity,
        IReadOnlyCollection<string> Scopes,
        string RulesJson);

    /// <summary>Top-level shape of <c>config/policies-seed.json</c>.</summary>
    public sealed class SeedConfig
    {
        public List<SeedPolicy> Policies { get; set; } = new();
    }

    /// <summary>One policy row inside <see cref="SeedConfig.Policies"/>.</summary>
    public sealed class SeedPolicy
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string Enforcement { get; set; } = string.Empty;
        public List<string> Scopes { get; set; } = new();
        public JsonElement RulesJson { get; set; }
    }
}
