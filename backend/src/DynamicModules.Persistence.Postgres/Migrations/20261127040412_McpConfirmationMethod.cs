using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class McpConfirmationMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeclinedUtc",
                table: "Dynamic_McpConfirmation",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Method",
                table: "Dynamic_McpConfirmation",
                type: "text",
                maxLength: 20,
                nullable: false,
                defaultValueSql: "'code'",
                collation: "C");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeclinedUtc",
                table: "Dynamic_McpConfirmation");

            migrationBuilder.DropColumn(
                name: "Method",
                table: "Dynamic_McpConfirmation");
        }
    }
}
