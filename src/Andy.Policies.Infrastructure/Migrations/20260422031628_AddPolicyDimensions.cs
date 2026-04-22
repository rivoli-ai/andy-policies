using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Policies.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPolicyDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Enforcement",
                table: "policy_versions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string[]>(
                name: "Scopes",
                table: "policy_versions",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "policy_versions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_policy_versions_Enforcement",
                table: "policy_versions",
                column: "Enforcement");

            migrationBuilder.CreateIndex(
                name: "IX_policy_versions_Severity",
                table: "policy_versions",
                column: "Severity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_policy_versions_Enforcement",
                table: "policy_versions");

            migrationBuilder.DropIndex(
                name: "IX_policy_versions_Severity",
                table: "policy_versions");

            migrationBuilder.DropColumn(
                name: "Enforcement",
                table: "policy_versions");

            migrationBuilder.DropColumn(
                name: "Scopes",
                table: "policy_versions");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "policy_versions");
        }
    }
}
