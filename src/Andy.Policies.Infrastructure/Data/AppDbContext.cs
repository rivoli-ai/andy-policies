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

    public DbSet<Binding> Bindings => Set<Binding>();

    public DbSet<ScopeNode> ScopeNodes => Set<ScopeNode>();

    public DbSet<Override> Overrides => Set<Override>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<Bundle> Bundles => Set<Bundle>();

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

        // P3.1 (rivoli-ai/andy-policies#19) — Binding metadata table.
        // - HasConversion<int> on the two enums so persisted ordinals match the
        //   enum definitions (load-bearing on disk).
        // - FK Restrict on PolicyVersionId: deleting a version with active
        //   bindings is rejected at the DB layer; consumers must soft-delete
        //   the bindings first.
        // - Three indexes:
        //     ix_bindings_target — every target-side lookup (P3.3, P3.4, P4
        //       hierarchy walk) is `WHERE TargetType = ? AND TargetRef = ?`,
        //       so a covering composite index keeps the hot path off table
        //       scans.
        //     ix_bindings_policy_version — version-side reads (list-by-version,
        //       cascade refusal in P3.2 when a version transitions to Retired).
        //     ix_bindings_deleted_at — partial-style filter for active-only
        //       queries; we keep it as a plain index on both providers since
        //       SQLite's partial-index syntax differs and the column has low
        //       cardinality.
        modelBuilder.Entity<Binding>(entity =>
        {
            entity.ToTable("bindings");
            entity.HasKey(b => b.Id);

            entity.Property(b => b.TargetType).HasConversion<int>().IsRequired();
            entity.Property(b => b.BindStrength).HasConversion<int>().IsRequired();
            entity.Property(b => b.TargetRef).IsRequired().HasMaxLength(512);
            entity.Property(b => b.CreatedBySubjectId).IsRequired().HasMaxLength(256);
            entity.Property(b => b.DeletedBySubjectId).HasMaxLength(256);

            entity.HasOne(b => b.PolicyVersion)
                .WithMany()
                .HasForeignKey(b => b.PolicyVersionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(b => new { b.TargetType, b.TargetRef })
                .HasDatabaseName("ix_bindings_target");
            entity.HasIndex(b => b.PolicyVersionId)
                .HasDatabaseName("ix_bindings_policy_version");
            entity.HasIndex(b => b.DeletedAt)
                .HasDatabaseName("ix_bindings_deleted_at");
        });

        // P4.1 (rivoli-ai/andy-policies#28) — ScopeNode hierarchy table.
        // - HasConversion<int> on Type so the persisted ordinal matches the
        //   enum definition (load-bearing on disk).
        // - FK Restrict on ParentId: deleting a parent that still has
        //   children is rejected at the DB layer; consumers must walk the
        //   subtree first.
        // - Three indexes:
        //     ix_scope_nodes_type_ref (unique) — uniqueness invariant from
        //       the issue spec; two nodes cannot both claim the same
        //       (Type, Ref) pair. Repeated Ref across types is permitted.
        //     ix_scope_nodes_parent_id — child lookups during walk-down /
        //       cycle prevention probes (P4.2).
        //     ix_scope_nodes_materialized_path — descendant lookup via
        //       LIKE '/root-id/%'; the materialized-path strategy lets
        //       both providers index the prefix scan.
        modelBuilder.Entity<ScopeNode>(entity =>
        {
            entity.ToTable("scope_nodes");
            entity.HasKey(s => s.Id);

            entity.Property(s => s.Type).HasConversion<int>().IsRequired();
            entity.Property(s => s.Ref).IsRequired().HasMaxLength(512);
            entity.Property(s => s.DisplayName).IsRequired().HasMaxLength(256);
            entity.Property(s => s.MaterializedPath).IsRequired().HasMaxLength(4096);
            entity.Property(s => s.Depth).IsRequired();

            entity.HasOne(s => s.Parent)
                .WithMany(s => s.Children)
                .HasForeignKey(s => s.ParentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(s => new { s.Type, s.Ref })
                .IsUnique()
                .HasDatabaseName("ix_scope_nodes_type_ref");
            entity.HasIndex(s => s.ParentId)
                .HasDatabaseName("ix_scope_nodes_parent_id");
            entity.HasIndex(s => s.MaterializedPath)
                .HasDatabaseName("ix_scope_nodes_materialized_path");
        });

        // P5.1 (rivoli-ai/andy-policies#49) — Override entity. Per-
        // principal or per-cohort escape hatch from stricter-tightens-
        // only resolution.
        // - HasConversion<string>() on the three enums so the partial
        //   index can filter on "State" = 'Approved' directly without an
        //   int-to-string cast (and so the persisted shape is human-
        //   readable for forensic queries).
        // - FK Restrict on PolicyVersionId AND ReplacementPolicyVersionId:
        //   deleting a version with active overrides is rejected at the
        //   DB layer; the override contract is reproducibility, not
        //   cascade.
        // - Two indexes:
        //     ix_overrides_scope_state — every (P4-resolution / P5
        //       service) read filters on (ScopeKind, ScopeRef, State);
        //       the composite covering index keeps the hot path off
        //       table scans.
        //     ix_overrides_expiry_approved — partial index used by the
        //       reaper (P5.3) to sweep `WHERE "State" = 'Approved'
        //       AND "ExpiresAt" < now()` without scanning the whole
        //       table. Postgres + SQLite both honour the same partial
        //       index syntax (string column + literal compare).
        // - CHECK constraint ck_overrides_effect_replacement: the
        //   Replace/Exempt invariant — Replace iff
        //   ReplacementPolicyVersionId is non-null. EF's HasCheckConstraint
        //   uses the column name in quotes for case-sensitive Postgres
        //   identifiers; SQLite is lenient about casing.
        // - Revision uint concurrency token, bumped manually in
        //   BumpRevisions below (matches the PolicyVersion pattern from
        //   ADR 0001).
        modelBuilder.Entity<Override>(entity =>
        {
            entity.ToTable("overrides", t =>
            {
                t.HasCheckConstraint(
                    "ck_overrides_effect_replacement",
                    "(\"Effect\" = 'Exempt' AND \"ReplacementPolicyVersionId\" IS NULL) OR " +
                    "(\"Effect\" = 'Replace' AND \"ReplacementPolicyVersionId\" IS NOT NULL)");
            });
            entity.HasKey(o => o.Id);

            entity.Property(o => o.ScopeKind).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(o => o.Effect).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(o => o.State).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(o => o.ScopeRef).IsRequired().HasMaxLength(256);
            entity.Property(o => o.ProposerSubjectId).IsRequired().HasMaxLength(128);
            entity.Property(o => o.ApproverSubjectId).HasMaxLength(128);
            entity.Property(o => o.Rationale).IsRequired().HasMaxLength(2000);
            entity.Property(o => o.RevocationReason).HasMaxLength(2000);
            entity.Property(o => o.Revision).IsConcurrencyToken();

            entity.HasOne(o => o.PolicyVersion)
                .WithMany()
                .HasForeignKey(o => o.PolicyVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(o => o.ReplacementPolicyVersion)
                .WithMany()
                .HasForeignKey(o => o.ReplacementPolicyVersionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(o => new { o.ScopeKind, o.ScopeRef, o.State })
                .HasDatabaseName("ix_overrides_scope_state");
            entity.HasIndex(o => o.ExpiresAt)
                .HasDatabaseName("ix_overrides_expiry_approved")
                .HasFilter("\"State\" = 'Approved'");
        });

        // P6.1 (#41): tamper-evident catalog audit log. The migration
        // emits provider-specific append-only enforcement (Postgres
        // trigger + REVOKE on the runtime app role; SQLite trigger);
        // the EF mapping just describes the shape and the indexes
        // P6.5/6.6 query against. Storage notes:
        //   - Seq is bigserial on Postgres / INTEGER AUTOINCREMENT on
        //     SQLite. P6.2's hash-chain verifier walks rows ordered
        //     by Seq (not by Timestamp; clocks can skew, sequence
        //     cannot).
        //   - PrevHash + Hash land as bytea on Postgres / BLOB on
        //     SQLite. Pinning byte length to 32 in the entity keeps
        //     the SHA-256 invariant readable in C#; the column type
        //     stays variable-length so a future hash upgrade
        //     doesn't need a schema migration.
        //   - ActorRoles travels as text[] on Postgres (native
        //     arrays make role-set queries trivial) and falls back
        //     to a comma-joined string on SQLite (handled via
        //     value converter so the entity surface stays string[]).
        //   - FieldDiffJson is jsonb on Postgres for the same
        //     query-shape reason; TEXT on SQLite.
        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(e => e.Id);

            // Seq is assigned explicitly by the AuditChain (P6.2)
            // under the advisory lock / process semaphore, so the
            // value comes from the writer rather than from a
            // bigserial/AUTOINCREMENT column. ValueGeneratedNever
            // keeps EF from synthesising an INSERT shape that
            // expects database-side generation.
            entity.Property(e => e.Seq)
                .HasColumnName("seq")
                .ValueGeneratedNever();
            entity.HasIndex(e => e.Seq)
                .IsUnique()
                .HasDatabaseName("ix_audit_events_seq");

            entity.Property(e => e.ActorSubjectId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Action).IsRequired().HasMaxLength(128);
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(64);
            entity.Property(e => e.EntityId).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Rationale).HasMaxLength(2000);

            entity.HasIndex(e => new { e.EntityType, e.EntityId })
                .HasDatabaseName("ix_audit_events_entity");
            entity.HasIndex(e => e.ActorSubjectId)
                .HasDatabaseName("ix_audit_events_actor");
            entity.HasIndex(e => e.Timestamp)
                .HasDatabaseName("ix_audit_events_timestamp");

            if (isNpgsql)
            {
                // Native bytea / jsonb / text[] on Postgres so P6.5+
                // queries can leverage GIN/B-Tree on the structured
                // columns directly.
                entity.Property(e => e.PrevHash).HasColumnType("bytea").IsRequired();
                entity.Property(e => e.Hash).HasColumnType("bytea").IsRequired();
                entity.Property(e => e.FieldDiffJson).HasColumnType("jsonb").IsRequired();
                entity.Property(e => e.ActorRoles).HasColumnType("text[]").IsRequired();
            }
            else
            {
                // SQLite fallback: BLOB for hashes, TEXT for the
                // patch document, and a delimited string for roles.
                // The roles converter splits on a control char that
                // can never appear in an RBAC role name.
                var rolesConverter = new ValueConverter<string[], string>(
                    arr => string.Join('', arr),
                    str => string.IsNullOrEmpty(str)
                        ? Array.Empty<string>()
                        : str.Split('', StringSplitOptions.None));
                var rolesComparer = new ValueComparer<string[]>(
                    (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                    arr => arr.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                    arr => arr.ToArray());
                entity.Property(e => e.ActorRoles)
                    .HasConversion(rolesConverter)
                    .Metadata.SetValueComparer(rolesComparer);
            }
        });

        // P8.1 (rivoli-ai/andy-policies#81) — Bundle: immutable
        // materialized snapshots of the catalog for reproducibility.
        // Storage notes:
        //   - SnapshotJson is the entire frozen graph (canonical-JSON
        //     bytes). Postgres maps it to jsonb so future P8 ops can
        //     index into it; SQLite (embedded mode) takes TEXT.
        //   - SnapshotHash is fixed-length SHA-256 hex. Pinning to
        //     CHAR(64) on Postgres lets the optimizer skip varchar
        //     size probes on the lookup-by-hash path; SQLite has no
        //     CHAR distinction so it stays TEXT.
        //   - State is stored as a string (mirrors LifecycleState +
        //     OverrideState) so the filtered unique index on Name
        //     can use a literal compare without an int-to-string
        //     cast.
        // Indexes:
        //   - ux_bundles_name_active: filtered unique index on Name
        //     (filter `"State" = 'Active'`) — Postgres + SQLite both
        //     honour the identical syntax, so a soft-deleted slug
        //     releases the name on both providers.
        //   - ix_bundles_state_created_at: list-view sort key
        //     (newest active first).
        //   - ix_bundles_snapshot_hash: lookup-by-hash for the audit
        //     cross-reference (the bundle.create event payload is
        //     this hash).
        modelBuilder.Entity<Bundle>(entity =>
        {
            entity.ToTable("bundles");
            entity.HasKey(b => b.Id);

            entity.Property(b => b.Name).IsRequired().HasMaxLength(64);
            entity.Property(b => b.Description).HasMaxLength(2048);
            entity.Property(b => b.CreatedBySubjectId).IsRequired().HasMaxLength(256);
            entity.Property(b => b.DeletedBySubjectId).HasMaxLength(256);

            entity.Property(b => b.State)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            entity.Property(b => b.SnapshotJson)
                .IsRequired()
                .HasColumnType(isNpgsql ? "jsonb" : "TEXT");

            entity.Property(b => b.SnapshotHash)
                .IsRequired()
                .HasColumnType(isNpgsql ? "char(64)" : "TEXT")
                .HasMaxLength(64);

            entity.HasIndex(b => new { b.State, b.CreatedAt })
                .HasDatabaseName("ix_bundles_state_created_at");

            entity.HasIndex(b => b.SnapshotHash)
                .HasDatabaseName("ix_bundles_snapshot_hash");

            // Filtered unique index — Postgres + SQLite (≥ 3.8) honour the
            // identical literal-compare filter, so a soft-deleted slug
            // releases the name for a new active bundle on both providers.
            // Same pattern as ix_policy_versions_one_draft_per_policy
            // above; see the comment block over PolicyVersion for the
            // string-storage rationale that makes this portable.
            entity.HasIndex(b => b.Name, "ux_bundles_name_active")
                .IsUnique()
                .HasFilter("\"State\" = 'Active'");
        });
    }

    // Override only the `bool`-flavoured routing entry points. EF routes `SaveChanges()` →
    // `SaveChanges(bool)` and `SaveChangesAsync(ct)` → `SaveChangesAsync(bool, ct)` internally,
    // so hooking here catches every path without double-invoking the guard + revision bump.

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforcePolicyVersionImmutability();
        EnforceBundleImmutability();
        BumpRevisions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforcePolicyVersionImmutability();
        EnforceBundleImmutability();
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
            nameof(PolicyVersion.RetiredAt),
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
    /// P8.1 (#81): a <see cref="Bundle"/> in <see cref="BundleState.Active"/>
    /// must not have any modified scalar property other than the soft-delete
    /// trio (<c>State</c>, <c>DeletedAt</c>, <c>DeletedBySubjectId</c>). The
    /// reproducibility contract — pinning a bundle id returns the same
    /// snapshot across all reads — depends on this; flipping a single byte
    /// of <c>SnapshotJson</c> after insert would invalidate
    /// <c>SnapshotHash</c> and silently change consumer answers.
    /// </summary>
    /// <remarks>
    /// We compare against the original <see cref="Bundle.State"/>: an
    /// already-tombstoned (<see cref="BundleState.Deleted"/>) row is
    /// frozen entirely — even the soft-delete columns can no longer
    /// change, since "delete a deletion" is not a state machine edge.
    /// </remarks>
    private void EnforceBundleImmutability()
    {
        var allowListedOnActive = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Bundle.State),
            nameof(Bundle.DeletedAt),
            nameof(Bundle.DeletedBySubjectId),
        };

        foreach (var entry in ChangeTracker.Entries<Bundle>())
        {
            if (entry.State != EntityState.Modified) continue;

            var originalState = (BundleState)entry.OriginalValues[nameof(Bundle.State)]!;

            foreach (var prop in entry.Properties)
            {
                if (!prop.IsModified) continue;

                // Soft-delete trio is the only legal mutation, and only on
                // an originally-Active bundle.
                if (originalState == BundleState.Active &&
                    allowListedOnActive.Contains(prop.Metadata.Name))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Bundle {entry.Entity.Id} is in state {originalState}; the snapshot is " +
                    $"immutable. Attempted change on '{prop.Metadata.Name}'. " +
                    $"Only the soft-delete flip (State/DeletedAt/DeletedBySubjectId on an " +
                    $"Active bundle) is permitted.");
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

        // P5.1: Override carries the same uint Revision concurrency
        // token; bump on every modification so the reaper (P5.3) and
        // approval workflow (P5.2) surface optimistic-concurrency
        // conflicts instead of silently overwriting each other.
        foreach (var entry in ChangeTracker.Entries<Override>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.Revision = unchecked(entry.Entity.Revision + 1);
            }
        }
    }
}
