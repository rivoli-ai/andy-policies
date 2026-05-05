using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Policies.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BundlePinning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Note: EF would also auto-generate an AlterColumn on
            // audit_events.seq here to scrub a stale Npgsql identity
            // annotation drift between AppDbContext and the prior
            // model snapshot (the runtime config uses
            // ValueGeneratedNever, the snapshot remembered
            // IdentityByDefaultColumn from #41 P6.1). That AlterColumn
            // is intentionally elided: on SQLite EF implements
            // ALTER COLUMN as table rebuild, which drops the
            // append-only triggers from #41. Fixing the drift is
            // tracked separately; this migration only adds the
            // bundles table.
            migrationBuilder.CreateTable(
                name: "bundles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBySubjectId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    SnapshotHash = table.Column<string>(type: "char(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedBySubjectId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bundles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bundles_snapshot_hash",
                table: "bundles",
                column: "SnapshotHash");

            migrationBuilder.CreateIndex(
                name: "ix_bundles_state_created_at",
                table: "bundles",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_bundles_name_active",
                table: "bundles",
                column: "Name",
                unique: true,
                filter: "\"State\" = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bundles");
        }
    }
}
