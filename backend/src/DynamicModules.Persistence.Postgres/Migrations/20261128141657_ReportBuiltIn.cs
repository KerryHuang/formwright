using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ReportBuiltIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "OwnerUserId",
                table: "Dynamic_Report",
                type: "text",
                maxLength: 50,
                nullable: true,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldMaxLength: 50,
                oldCollation: "C");

            migrationBuilder.AddColumn<string>(
                name: "BuiltInCode",
                table: "Dynamic_Report",
                type: "text",
                maxLength: 64,
                nullable: true,
                collation: "C");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Report_BuiltInCode",
                table: "Dynamic_Report",
                column: "BuiltInCode",
                unique: true,
                filter: "\"BuiltInCode\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Report_OwnerOrBuiltIn",
                table: "Dynamic_Report",
                sql: "\"OwnerUserId\" IS NOT NULL OR \"BuiltInCode\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Dynamic_Report_BuiltInCode",
                table: "Dynamic_Report");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Report_OwnerOrBuiltIn",
                table: "Dynamic_Report");

            migrationBuilder.DropColumn(
                name: "BuiltInCode",
                table: "Dynamic_Report");

            migrationBuilder.AlterColumn<string>(
                name: "OwnerUserId",
                table: "Dynamic_Report",
                type: "text",
                maxLength: 50,
                nullable: false,
                defaultValue: "",
                collation: "C",
                oldClrType: typeof(string),
                oldType: "text",
                oldMaxLength: 50,
                oldNullable: true,
                oldCollation: "C");
        }
    }
}
