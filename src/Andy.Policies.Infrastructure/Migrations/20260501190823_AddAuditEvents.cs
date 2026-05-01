using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Andy.Policies.Infrastructure.Migrations
{
    /// <summary>
    /// P6.1 (rivoli-ai/andy-policies#41) — adds the <c>audit_events</c>
    /// table that backs the tamper-evident catalog audit chain
    /// (P6.2+). Append-only enforcement is provider-aware:
    /// <list type="bullet">
    ///   <item>Postgres — a BEFORE UPDATE/DELETE/TRUNCATE trigger
    ///     raises an exception, plus REVOKE on the runtime app role
    ///     pinned to the <c>andy_policies_app</c> role when it
    ///     exists. The two-user model (migrator vs app) is the
    ///     primary defence; the trigger is belt-and-braces.</item>
    ///   <item>SQLite — three triggers (UPDATE, DELETE, TRUNCATE
    ///     emulation) raise <c>ABORT</c>. Embedded mode is
    ///     single-user by design, so the trigger is the only
    ///     enforcement mechanism (documented in ADR 0006).</item>
    /// </list>
    /// </summary>
    public partial class AddAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Schema (Postgres-flavored — migration is generated against
            // the design-time Postgres provider; SQLite tolerates the
            // type names via its dynamic typing).
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PrevHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorSubjectId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ActorRoles = table.Column<string[]>(type: "text[]", nullable: false),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FieldDiffJson = table.Column<string>(type: "jsonb", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor",
                table: "audit_events",
                column: "ActorSubjectId");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_entity",
                table: "audit_events",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_seq",
                table: "audit_events",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_timestamp",
                table: "audit_events",
                column: "Timestamp");

            // -- Append-only enforcement ------------------------------
            //
            // ActiveProvider is resolved at apply time (the runtime
            // provider, not the design-time one), so we can branch
            // between Postgres and SQLite from a single migration.
            // Each branch is idempotent at the DDL level so a
            // re-application onto an existing audit-table-bearing
            // database does not fail (matters for testcontainer
            // setups that share volumes between test runs).
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"
                    CREATE OR REPLACE FUNCTION audit_events_no_mutate() RETURNS trigger AS $$
                    BEGIN
                        RAISE EXCEPTION 'audit_events is append-only';
                    END;
                    $$ LANGUAGE plpgsql;

                    DROP TRIGGER IF EXISTS trg_audit_events_no_update ON audit_events;
                    CREATE TRIGGER trg_audit_events_no_update
                        BEFORE UPDATE OR DELETE OR TRUNCATE ON audit_events
                        FOR EACH STATEMENT EXECUTE FUNCTION audit_events_no_mutate();

                    -- Pin the REVOKE to the named runtime role when it
                    -- exists (production/staging). Falls back to a
                    -- no-op when the schema is being migrated by the
                    -- same user that runs the app (dev / single-user
                    -- mode) — the trigger above still applies.
                    DO $$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'andy_policies_app') THEN
                            REVOKE UPDATE, DELETE, TRUNCATE ON TABLE audit_events FROM andy_policies_app;
                            GRANT INSERT, SELECT ON TABLE audit_events TO andy_policies_app;
                        END IF;
                    END $$;

                    REVOKE UPDATE, DELETE, TRUNCATE ON TABLE audit_events FROM PUBLIC;
                ");
            }
            else if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                // SQLite has no roles. The trigger is the only
                // enforcement mechanism. Three triggers (UPDATE,
                // DELETE — TRUNCATE doesn't exist in SQLite) each
                // raise ABORT with the same canonical message so
                // P6.2+ can match on text without parsing differing
                // sqlite_master output.
                migrationBuilder.Sql(@"
                    CREATE TRIGGER IF NOT EXISTS trg_audit_events_no_update
                        BEFORE UPDATE ON audit_events
                        BEGIN
                            SELECT RAISE(ABORT, 'audit_events is append-only');
                        END;

                    CREATE TRIGGER IF NOT EXISTS trg_audit_events_no_delete
                        BEFORE DELETE ON audit_events
                        BEGIN
                            SELECT RAISE(ABORT, 'audit_events is append-only');
                        END;
                ");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop triggers + role grants before the table so the
            // DropTable doesn't trip the trigger or hit a permissions
            // wall.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"
                    DROP TRIGGER IF EXISTS trg_audit_events_no_update ON audit_events;
                    DROP FUNCTION IF EXISTS audit_events_no_mutate();
                ");
            }
            else if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql(@"
                    DROP TRIGGER IF EXISTS trg_audit_events_no_update;
                    DROP TRIGGER IF EXISTS trg_audit_events_no_delete;
                ");
            }

            migrationBuilder.DropTable(
                name: "audit_events");
        }
    }
}
