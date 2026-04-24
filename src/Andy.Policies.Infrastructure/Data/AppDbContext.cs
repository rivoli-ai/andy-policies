// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Andy.Policies.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Item> Items => Set<Item>();

    public DbSet<Policy> Policies => Set<Policy>();

    public DbSet<PolicyVersion> PolicyVersions => Set<PolicyVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Item>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Description).HasMaxLength(2048);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(256);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.Status);
        });

        var isNpgsql = Database.IsNpgsql();

        modelBuilder.Entity<Policy>(entity =>
        {
            entity.ToTable("policies");
            entity.HasKey(p => p.Id);

            entity.Property(p => p.Name).IsRequired().HasMaxLength(64);
            entity.Property(p => p.Description).HasMaxLength(2048);
            entity.Property(p => p.CreatedBySubjectId).IsRequired().HasMaxLength(256);

            entity.HasIndex(p => p.Name).IsUnique();
        });

        modelBuilder.Entity<PolicyVersion>(entity =>
        {
            entity.ToTable("policy_versions");
            entity.HasKey(v => v.Id);

            entity.Property(v => v.Version).IsRequired();

            // State stored as string so migrations + filtered-unique-index SQL is portable
            // across Postgres and SQLite (both honour the `WHERE "State" = 'Draft'` filter literal).
            // The PascalCase column name is double-quoted so Postgres preserves case — unquoted
            // `state` resolves to a non-existent lowercase column on Postgres (case-folding rule).
            // SQLite is case-insensitive for identifiers either way.
            entity.Property(v => v.State)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(v => v.Summary).HasMaxLength(2048);
            entity.Property(v => v.RulesJson)
                .IsRequired()
                .HasColumnType(isNpgsql ? "jsonb" : "TEXT");

            // Dimensions (ADR 0001 §6): RFC-2119 posture, triage tier, applicability tags.
            entity.Property(v => v.Enforcement)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            entity.Property(v => v.Severity)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            // Scopes: Postgres gets a native text[] (zero-query membership checks via ANY()).
            // SQLite has no array type — we use a `|`-delimited string via a value converter.
            // Scope elements are constrained by PolicyScope.RegexPattern (no `|`) so the
            // delimiter is safe.
            if (isNpgsql)
            {
                entity.Property(v => v.Scopes)
                    .HasColumnType("text[]")
                    .IsRequired();
            }
            else
            {
                var converter = new ValueConverter<IList<string>, string>(
                    v => string.Join('|', v),
                    v => v.Length == 0
                        ? new List<string>()
                        : v.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList());

                var comparer = new ValueComparer<IList<string>>(
                    (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
                    v => v == null
                        ? 0
                        : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                    v => v.ToList());

                entity.Property(v => v.Scopes)
                    .HasConversion(converter)
                    .Metadata.SetValueComparer(comparer);
                entity.Property(v => v.Scopes).IsRequired();
            }

            entity.HasIndex(v => v.Enforcement);
            entity.HasIndex(v => v.Severity);

            entity.Property(v => v.CreatedBySubjectId).IsRequired().HasMaxLength(256);
            entity.Property(v => v.ProposerSubjectId).IsRequired().HasMaxLength(256);
            entity.Property(v => v.PublishedBySubjectId).HasMaxLength(256);

            entity.Property(v => v.Revision).IsRequired();

            entity.HasOne(v => v.Policy)
                .WithMany(p => p.Versions)
                .HasForeignKey(v => v.PolicyId)
                .OnDelete(DeleteBehavior.Cascade);

            // Composite uniqueness: one row per (PolicyId, Version). Gaps in Version are
            // disallowed at the service layer (P1.4).
            entity.HasIndex(v => new { v.PolicyId, v.Version }).IsUnique();

            // Partial unique indexes enforcing the ADR 0001 §4 (one open Draft per policy) and
            // ADR 0002 §4 (only one Active per policy) invariants at the DB level. Both Postgres
            // and SQLite (≥ 3.8.0) honour this filter syntax identically when the column is a
            // string column; we picked string storage for State specifically for this reason.
            //
            // The explicit two-argument `HasIndex(expression, name)` overload is required: two
            // `HasIndex(v => v.PolicyId)` calls with only `HasDatabaseName` would collide into a
            // single index in the EF model (matched by column list). Passing a distinct name
            // via the overload registers them as separate logical indexes.
            entity.HasIndex(v => v.PolicyId, "ix_policy_versions_one_draft_per_policy")
                .IsUnique()
                .HasFilter("\"State\" = 'Draft'");

            entity.HasIndex(v => v.PolicyId, "ix_policy_versions_one_active_per_policy")
                .IsUnique()
                .HasFilter("\"State\" = 'Active'");
        });

        // Optimistic-concurrency token: a uniform <c>uint Revision</c> column on both providers,
        // bumped manually in <see cref="SaveChangesAsync"/>. ADR 0001 §7 originally called out
        // Postgres xmin mapping via <c>UseXminAsConcurrencyToken()</c>, but that API was marked
        // obsolete in EF Core 8 (Npgsql now steers contributors to <c>IsRowVersion()</c> or a
        // manual token). A manual uint is the simpler cross-provider path and keeps the column
        // visible to integration tests; a follow-up story may revisit if throughput on hot
        // paths warrants the xmin optimisation.
        modelBuilder.Entity<PolicyVersion>()
            .Property(v => v.Revision)
            .IsConcurrencyToken();
    }

    // Override only the `bool`-flavoured routing entry points. EF routes `SaveChanges()` →
    // `SaveChanges(bool)` and `SaveChangesAsync(ct)` → `SaveChangesAsync(bool, ct)` internally,
    // so hooking here catches every path without double-invoking the guard + revision bump.

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforcePolicyVersionImmutability();
        BumpRevisions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforcePolicyVersionImmutability();
        BumpRevisions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Enforce ADR 0001 §3: a <see cref="PolicyVersion"/> whose original <c>State</c> is not
    /// <see cref="LifecycleState.Draft"/> may not have any content property modified.
    /// State-transition writes (<c>State</c>, <c>PublishedAt</c>, <c>PublishedBySubjectId</c>,
    /// <c>SupersededByVersionId</c>, <c>Revision</c>) are explicitly allow-listed so P2's
    /// transition service can supersede/retire versions.
    /// </summary>
    private void EnforcePolicyVersionImmutability()
    {
        var allowListedOnNonDraft = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(PolicyVersion.State),
            nameof(PolicyVersion.PublishedAt),
            nameof(PolicyVersion.PublishedBySubjectId),
            nameof(PolicyVersion.SupersededByVersionId),
            nameof(PolicyVersion.Revision),
            // Shadow properties (e.g. Npgsql's `xmin` concurrency token if ever re-enabled)
            // are never user-supplied; treat them as allow-listed so state-transition writes
            // that happen to touch shadow props do not throw.
        };

        foreach (var entry in ChangeTracker.Entries<PolicyVersion>())
        {
            if (entry.State != EntityState.Modified) continue;

            // Compare against the *original* State value. If the original was Draft, any field
            // may be freely modified (including a State transition out of Draft via P2). If the
            // original was non-Draft, only allow-listed transition properties may change.
            var originalState = (LifecycleState)entry.OriginalValues[nameof(PolicyVersion.State)]!;
            if (originalState == LifecycleState.Draft) continue;

            foreach (var prop in entry.Properties)
            {
                if (!prop.IsModified) continue;
                if (allowListedOnNonDraft.Contains(prop.Metadata.Name)) continue;

                throw new InvalidOperationException(
                    $"PolicyVersion {entry.Entity.Id} is in state {originalState}; " +
                    $"only Draft versions are mutable. Attempted change on '{prop.Metadata.Name}'.");
            }
        }
    }

    /// <summary>
    /// Concurrency-token maintenance: bump <see cref="PolicyVersion.Revision"/> on every
    /// modification so optimistic-concurrency conflicts surface as <c>DbUpdateConcurrencyException</c>.
    /// Applied uniformly on both providers (see the OnModelCreating note about the deprecated
    /// xmin path).
    /// </summary>
    private void BumpRevisions()
    {
        foreach (var entry in ChangeTracker.Entries<PolicyVersion>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.Revision = unchecked(entry.Entity.Revision + 1);
            }
        }
    }
}
