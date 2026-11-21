using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_BackupRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ScheduleSlot = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    LocationId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ActiveSlot = table.Column<int>(type: "INTEGER", nullable: true),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    RequestedUserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    QueuedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HeartbeatUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SnapshotUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OwnerInstanceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    StoreKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SetName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AppVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MigrationId = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    DatabaseBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    AttachmentCount = table.Column<long>(type: "INTEGER", nullable: true),
                    NewAttachmentCount = table.Column<long>(type: "INTEGER", nullable: true),
                    NewAttachmentBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalAttachmentBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    SkippedAttachments = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ErrorDetail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    PruneError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    PrunedSets = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_BackupRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_BackupState",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LocationId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AdoptedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AdoptedByInstanceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_BackupState", x => x.Kind);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_OpsLock",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UntilUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_OpsLock", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_BackupRun_ActiveSlot",
                table: "Dynamic_BackupRun",
                column: "ActiveSlot",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_BackupRun_LocationId_Status_FinishedUtc",
                table: "Dynamic_BackupRun",
                columns: new[] { "LocationId", "Status", "FinishedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_BackupRun_RunKey",
                table: "Dynamic_BackupRun",
                column: "RunKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_BackupRun_ScheduleSlot",
                table: "Dynamic_BackupRun",
                column: "ScheduleSlot");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_BackupRun");

            migrationBuilder.DropTable(
                name: "Dynamic_BackupState");

            migrationBuilder.DropTable(
                name: "Dynamic_OpsLock");
        }
    }
}
