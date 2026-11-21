using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppBaselines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_AppBaseline",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "INTEGER", nullable: false),
                    PackageJson = table.Column<string>(type: "TEXT", nullable: false),
                    GroupMapJson = table.Column<string>(type: "TEXT", nullable: false),
                    ExcludedFieldsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SkippedJson = table.Column<string>(type: "TEXT", nullable: false),
                    CapturedTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppBaseline", x => x.AppId);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppBaseline_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_AppBaseline");
        }
    }
}
