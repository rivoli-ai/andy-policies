using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Policies.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScopeNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scope_nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MaterializedPath = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scope_nodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scope_nodes_scope_nodes_ParentId",
                        column: x => x.ParentId,
                        principalTable: "scope_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scope_nodes_materialized_path",
                table: "scope_nodes",
                column: "MaterializedPath");

            migrationBuilder.CreateIndex(
                name: "ix_scope_nodes_parent_id",
                table: "scope_nodes",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "ix_scope_nodes_type_ref",
                table: "scope_nodes",
                columns: new[] { "Type", "Ref" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scope_nodes");
        }
    }
}
