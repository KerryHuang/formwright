using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DynamicModules.Persistence.Postgres.Migrations
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunKey = table.Column<string>(type: "text", maxLength: 32, nullable: false, collation: "C"),
                    Trigger = table.Column<string>(type: "text", maxLength: 16, nullable: false, collation: "C"),
                    ScheduleSlot = table.Column<string>(type: "text", maxLength: 10, nullable: true, collation: "C"),
                    LocationId = table.Column<string>(type: "text", maxLength: 32, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "text", maxLength: 16, nullable: false, collation: "C"),
                    ActiveSlot = table.Column<int>(type: "integer", nullable: true),
                    Stage = table.Column<string>(type: "text", maxLength: 16, nullable: true, collation: "C"),
                    RequestedUserId = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    QueuedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HeartbeatUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SnapshotUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OwnerInstanceId = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    StoreKind = table.Column<string>(type: "text", maxLength: 32, nullable: false, collation: "C"),
                    SetName = table.Column<string>(type: "text", maxLength: 64, nullable: true, collation: "C"),
                    Provider = table.Column<string>(type: "text", maxLength: 16, nullable: false, collation: "C"),
                    AppVersion = table.Column<string>(type: "text", maxLength: 64, nullable: true, collation: "C"),
                    MigrationId = table.Column<string>(type: "text", maxLength: 150, nullable: true, collation: "C"),
                    DatabaseBytes = table.Column<long>(type: "bigint", nullable: true),
                    AttachmentCount = table.Column<long>(type: "bigint", nullable: true),
                    NewAttachmentCount = table.Column<long>(type: "bigint", nullable: true),
                    NewAttachmentBytes = table.Column<long>(type: "bigint", nullable: true),
                    TotalAttachmentBytes = table.Column<long>(type: "bigint", nullable: true),
                    SkippedAttachments = table.Column<long>(type: "bigint", nullable: true),
                    ErrorCode = table.Column<string>(type: "text", maxLength: 32, nullable: true, collation: "C"),
                    ErrorDetail = table.Column<string>(type: "text", maxLength: 1000, nullable: true, collation: "C"),
                    PruneError = table.Column<string>(type: "text", maxLength: 1000, nullable: true, collation: "C"),
                    PrunedSets = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_BackupRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_BackupState",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    LocationId = table.Column<string>(type: "text", maxLength: 32, nullable: true, collation: "C"),
                    AdoptedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AdoptedByInstanceId = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_BackupState", x => x.Kind);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_OpsLock",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", maxLength: 32, nullable: false, collation: "C"),
                    Owner = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    UntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
