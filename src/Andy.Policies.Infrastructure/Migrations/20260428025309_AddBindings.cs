using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Policies.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<int>(type: "integer", nullable: false),
                    TargetRef = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    BindStrength = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBySubjectId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedBySubjectId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bindings_policy_versions_PolicyVersionId",
                        column: x => x.PolicyVersionId,
                        principalTable: "policy_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bindings_deleted_at",
                table: "bindings",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "ix_bindings_policy_version",
                table: "bindings",
                column: "PolicyVersionId");

            migrationBuilder.CreateIndex(
                name: "ix_bindings_target",
                table: "bindings",
                columns: new[] { "TargetType", "TargetRef" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bindings");
        }
    }
}
