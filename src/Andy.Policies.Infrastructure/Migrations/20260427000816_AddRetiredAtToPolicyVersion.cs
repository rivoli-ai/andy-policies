using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Policies.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRetiredAtToPolicyVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetiredAt",
                table: "policy_versions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetiredAt",
                table: "policy_versions");
        }
    }
}
