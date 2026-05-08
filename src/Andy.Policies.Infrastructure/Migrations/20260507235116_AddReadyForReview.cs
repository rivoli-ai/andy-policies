using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Policies.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReadyForReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #216 — author-driven "ready for review" handoff signal.
            // Default false on backfill so the column is non-null without
            // a runtime backfill pass; existing drafts stay at false until
            // an author explicitly proposes them.
            migrationBuilder.AddColumn<bool>(
                name: "ReadyForReview",
                table: "policy_versions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Composite index for the approver-inbox query
            // `(State == Draft && ReadyForReview == true)`. Portable across
            // Postgres + SQLite; see AppDbContext for the rationale on
            // not using a partial filter.
            migrationBuilder.CreateIndex(
                name: "ix_policy_versions_pending_approval",
                table: "policy_versions",
                columns: new[] { "State", "ReadyForReview" });

            // NOTE: EF would also auto-generate an AlterColumn on
            // audit_events.seq to scrub a stale Npgsql identity
            // annotation drift between AppDbContext and the prior
            // model snapshot (the runtime config uses
            // ValueGeneratedNever, the snapshot remembered
            // IdentityByDefaultColumn from P6.1 #41). That AlterColumn
            // is intentionally elided here for the same reason as
            // BundlePinning #81: on SQLite EF implements ALTER COLUMN
            // as a table rebuild, which would drop the append-only
            // triggers from P6.1. The drift is harmless at runtime
            // (Npgsql honours ValueGeneratedNever() over the stale
            // identity annotation) and tracked separately for a
            // dedicated cleanup migration.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_policy_versions_pending_approval",
                table: "policy_versions");

            migrationBuilder.DropColumn(
                name: "ReadyForReview",
                table: "policy_versions");
        }
    }
}
