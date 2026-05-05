// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Andy.Policies.Tests.Integration.Persistence;

/// <summary>
/// P8.1 (#81) integration tests for the <see cref="Bundle"/> table:
/// migration applies on SQLite + Postgres, the immutability sweep in
/// <see cref="AppDbContext.SaveChangesAsync"/> rejects content
/// mutations on an active bundle, and the soft-delete state flip is
/// the one allow-listed mutation.
/// </summary>
/// <remarks>
/// The Postgres-specific facts (filtered unique index, jsonb column
/// type) are <see cref="SkippableFactAttribute"/>-gated on Docker
/// availability — same pattern as <c>PostgresMigrationTests</c>.
/// </remarks>
public class BundleMigrationTests : IAsyncLifetime, IDisposable
{
    // SQLite: file-backed so the database lives across DbContext instances
    // (some tests reopen the context to verify a re-read state).
    private readonly string _sqlitePath;
    private readonly SqliteConnection _sqliteConnection;

    // Postgres: container started in InitializeAsync; null + skip on
    // hosts without Docker.
    private PostgreSqlContainer? _pgContainer;
    private string _pgConnectionString = string.Empty;
    private bool _dockerAvailable;

    public BundleMigrationTests()
    {
        _sqlitePath = Path.Combine(Path.GetTempPath(), $"andy-policies-bundlemig-{Guid.NewGuid():N}.db");
        _sqliteConnection = new SqliteConnection($"Data Source={_sqlitePath};Foreign Keys=true");
        _sqliteConnection.Open();
    }

    public async Task InitializeAsync()
    {
        try
        {
            _pgContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("andy_policies_bundle_test")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
            await _pgContainer.StartAsync();
            _pgConnectionString = _pgContainer.GetConnectionString();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_pgContainer is not null) await _pgContainer.DisposeAsync();
    }

    public void Dispose()
    {
        _sqliteConnection.Dispose();
        if (File.Exists(_sqlitePath)) File.Delete(_sqlitePath);
    }

    private DbContextOptions<AppDbContext> NewSqliteOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_sqliteConnection)
            .Options;

    private DbContextOptions<AppDbContext> NewPostgresOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_pgConnectionString)
            .Options;

    private static Bundle SeedBundle(string name = "snap-1") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = "fixture",
        SnapshotJson = """{"schemaVersion":"1"}""",
        SnapshotHash = new string('a', 64),
        State = BundleState.Active,
        CreatedBySubjectId = "test",
    };

    // ----- SQLite path -----------------------------------------------------

    [Fact]
    public async Task Migration_AppliesCleanly_OnSqlite_AndCreatesBundlesTable()
    {
        await using var db = new AppDbContext(NewSqliteOptions());

        await db.Database.MigrateAsync();

        // sqlite_master pins the table + the three indexes.
        var indexes = await db.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='bundles'")
            .ToListAsync();
        indexes.Should().Contain(new[]
        {
            "ix_bundles_state_created_at",
            "ix_bundles_snapshot_hash",
            "ux_bundles_name_active",
        });
    }

    [Fact]
    public async Task Insert_BundleRoundTripsAllFields_OnSqlite()
    {
        await using var db = new AppDbContext(NewSqliteOptions());
        await db.Database.MigrateAsync();

        var bundle = SeedBundle("rt-1");
        db.Bundles.Add(bundle);
        await db.SaveChangesAsync();

        var reloaded = await db.Bundles.AsNoTracking().FirstAsync(b => b.Id == bundle.Id);
        reloaded.Name.Should().Be("rt-1");
        reloaded.SnapshotJson.Should().Be("""{"schemaVersion":"1"}""");
        reloaded.SnapshotHash.Should().Be(new string('a', 64));
        reloaded.State.Should().Be(BundleState.Active);
        reloaded.DeletedAt.Should().BeNull();
        reloaded.DeletedBySubjectId.Should().BeNull();
    }

    [Fact]
    public async Task SaveChanges_OnActiveBundle_RejectsSnapshotMutation()
    {
        await using var db = new AppDbContext(NewSqliteOptions());
        await db.Database.MigrateAsync();
        var bundle = SeedBundle("immut");
        db.Bundles.Add(bundle);
        await db.SaveChangesAsync();

        bundle.SnapshotJson = """{"schemaVersion":"1","tampered":true}""";

        var act = async () => await db.SaveChangesAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Bundle*immutable*SnapshotJson*",
                "the reproducibility contract depends on snapshot bytes never " +
                "changing post-insert; a successful save here would silently " +
                "rewrite consumer answers");
    }

    [Fact]
    public async Task SaveChanges_OnActiveBundle_RejectsNameMutation()
    {
        await using var db = new AppDbContext(NewSqliteOptions());
        await db.Database.MigrateAsync();
        var bundle = SeedBundle("rename-original");
        db.Bundles.Add(bundle);
        await db.SaveChangesAsync();

        bundle.Name = "rename-after";

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SaveChanges_OnActiveBundle_AllowsSoftDeleteFlip()
    {
        await using var db = new AppDbContext(NewSqliteOptions());
        await db.Database.MigrateAsync();
        var bundle = SeedBundle("softdel");
        db.Bundles.Add(bundle);
        await db.SaveChangesAsync();

        bundle.State = BundleState.Deleted;
        bundle.DeletedAt = DateTimeOffset.UtcNow;
        bundle.DeletedBySubjectId = "operator";

        await db.SaveChangesAsync();   // must not throw

        var reloaded = await db.Bundles.AsNoTracking().FirstAsync(b => b.Id == bundle.Id);
        reloaded.State.Should().Be(BundleState.Deleted);
        reloaded.DeletedBySubjectId.Should().Be("operator");
    }

    [Fact]
    public async Task SaveChanges_OnDeletedBundle_RejectsAnyFurtherMutation()
    {
        // Once tombstoned the row is fully frozen — there is no
        // "undelete" edge in the state machine.
        await using var db = new AppDbContext(NewSqliteOptions());
        await db.Database.MigrateAsync();
        var bundle = SeedBundle("frozen");
        db.Bundles.Add(bundle);
        await db.SaveChangesAsync();

        bundle.State = BundleState.Deleted;
        bundle.DeletedAt = DateTimeOffset.UtcNow;
        bundle.DeletedBySubjectId = "operator";
        await db.SaveChangesAsync();

        bundle.DeletedBySubjectId = "rewrite-history";

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task FilteredUniqueIndex_OnSqlite_AllowsNameReuseAfterSoftDelete()
    {
        // SQLite ≥ 3.8 honours the same filtered-unique syntax as
        // Postgres, so a soft-deleted slug releases the name without
        // a service-layer precheck. P8.2 still validates slug shape +
        // emptiness, but the soft-delete reuse path is enforced at
        // the DB level on both providers.
        await using var db = new AppDbContext(NewSqliteOptions());
        await db.Database.MigrateAsync();
        var first = SeedBundle("reuse-sqlite");
        db.Bundles.Add(first);
        await db.SaveChangesAsync();

        first.State = BundleState.Deleted;
        first.DeletedAt = DateTimeOffset.UtcNow;
        first.DeletedBySubjectId = "op";
        await db.SaveChangesAsync();

        db.Bundles.Add(SeedBundle("reuse-sqlite"));
        await db.SaveChangesAsync();   // must not throw

        var actives = await db.Bundles.AsNoTracking()
            .Where(b => b.Name == "reuse-sqlite" && b.State == BundleState.Active)
            .CountAsync();
        actives.Should().Be(1);
    }

    [Fact]
    public async Task FilteredUniqueIndex_OnSqlite_RejectsTwoActiveBundlesWithSameName()
    {
        await using var db = new AppDbContext(NewSqliteOptions());
        await db.Database.MigrateAsync();
        db.Bundles.Add(SeedBundle("clash-sqlite"));
        await db.SaveChangesAsync();

        db.Bundles.Add(SeedBundle("clash-sqlite"));
        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    // ----- Postgres path ---------------------------------------------------

    [SkippableFact]
    public async Task Migration_AppliesCleanly_OnPostgres()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(NewPostgresOptions());
        await db.Database.MigrateAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(m => m.EndsWith("_BundlePinning", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task SnapshotJson_Column_Is_Jsonb_OnPostgres()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(NewPostgresOptions());
        await db.Database.MigrateAsync();

        var udtName = (await db.Database.SqlQueryRaw<string>(
                """
                SELECT udt_name
                FROM information_schema.columns
                WHERE table_name = 'bundles' AND column_name = 'SnapshotJson'
                """).ToListAsync()).Single();

        udtName.Should().Be("jsonb");
    }

    [SkippableFact]
    public async Task FilteredUniqueIndex_OnPostgres_AllowsNameReuseAfterSoftDelete()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(NewPostgresOptions());
        await db.Database.MigrateAsync();

        var first = SeedBundle("reuse");
        db.Bundles.Add(first);
        await db.SaveChangesAsync();

        // Soft-delete: filter clause excludes the row from uniqueness.
        first.State = BundleState.Deleted;
        first.DeletedAt = DateTimeOffset.UtcNow;
        first.DeletedBySubjectId = "op";
        await db.SaveChangesAsync();

        // Now a fresh active bundle with the same name must succeed.
        db.Bundles.Add(SeedBundle("reuse"));
        await db.SaveChangesAsync();

        var actives = await db.Bundles.AsNoTracking()
            .Where(b => b.Name == "reuse" && b.State == BundleState.Active)
            .CountAsync();
        actives.Should().Be(1);
    }

    [SkippableFact]
    public async Task FilteredUniqueIndex_OnPostgres_RejectsTwoActiveBundlesWithSameName()
    {
        Skip.IfNot(_dockerAvailable);

        await using var db = new AppDbContext(NewPostgresOptions());
        await db.Database.MigrateAsync();
        db.Bundles.Add(SeedBundle("clash"));
        await db.SaveChangesAsync();

        db.Bundles.Add(SeedBundle("clash"));
        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
