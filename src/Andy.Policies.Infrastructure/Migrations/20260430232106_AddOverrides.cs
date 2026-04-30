using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Policies.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "overrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScopeRef = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Effect = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReplacementPolicyVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProposerSubjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ApproverSubjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProposedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RevocationReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overrides", x => x.Id);
                    table.CheckConstraint("ck_overrides_effect_replacement", "(\"Effect\" = 'Exempt' AND \"ReplacementPolicyVersionId\" IS NULL) OR (\"Effect\" = 'Replace' AND \"ReplacementPolicyVersionId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_overrides_policy_versions_PolicyVersionId",
                        column: x => x.PolicyVersionId,
                        principalTable: "policy_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_overrides_policy_versions_ReplacementPolicyVersionId",
                        column: x => x.ReplacementPolicyVersionId,
                        principalTable: "policy_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_overrides_expiry_approved",
                table: "overrides",
                column: "ExpiresAt",
                filter: "\"State\" = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "IX_overrides_PolicyVersionId",
                table: "overrides",
                column: "PolicyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_overrides_ReplacementPolicyVersionId",
                table: "overrides",
                column: "ReplacementPolicyVersionId");

            migrationBuilder.CreateIndex(
                name: "ix_overrides_scope_state",
                table: "overrides",
                columns: new[] { "ScopeKind", "ScopeRef", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "overrides");
        }
    }
}
