using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Dynamic_AppUser",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerifiedUtc",
                table: "Dynamic_AppUser",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TotpEnabledUtc",
                table: "Dynamic_AppUser",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotpLastStep",
                table: "Dynamic_AppUser",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TotpPendingProtected",
                table: "Dynamic_AppUser",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TotpPendingUtc",
                table: "Dynamic_AppUser",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TotpSecretProtected",
                table: "Dynamic_AppUser",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TwoFactorFailedCount",
                table: "Dynamic_AppUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "TwoFactorGraceStartUtc",
                table: "Dynamic_AppUser",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TwoFactorLockedUntilUtc",
                table: "Dynamic_AppUser",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Dynamic_AccountToken",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedReason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AccountToken", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_MailOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ToAddress = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Locale = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    ParamsJson = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NotAfterUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ClaimedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ClaimedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PreviewBody = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_MailOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_TrustedDevice",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_TrustedDevice", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_UserRecoveryCode",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_UserRecoveryCode", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AccountToken_ExpiresUtc",
                table: "Dynamic_AccountToken",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AccountToken_TokenHash",
                table: "Dynamic_AccountToken",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AccountToken_UserId_Purpose",
                table: "Dynamic_AccountToken",
                columns: new[] { "UserId", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_MailOutbox_CreatedUtc",
                table: "Dynamic_MailOutbox",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_MailOutbox_Status_NextAttemptUtc",
                table: "Dynamic_MailOutbox",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_MailOutbox_UserId_Status",
                table: "Dynamic_MailOutbox",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TrustedDevice_ExpiresUtc",
                table: "Dynamic_TrustedDevice",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TrustedDevice_TokenHash",
                table: "Dynamic_TrustedDevice",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TrustedDevice_UserId",
                table: "Dynamic_TrustedDevice",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_UserRecoveryCode_UserId_CodeHash",
                table: "Dynamic_UserRecoveryCode",
                columns: new[] { "UserId", "CodeHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_AccountToken");

            migrationBuilder.DropTable(
                name: "Dynamic_MailOutbox");

            migrationBuilder.DropTable(
                name: "Dynamic_TrustedDevice");

            migrationBuilder.DropTable(
                name: "Dynamic_UserRecoveryCode");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "EmailVerifiedUtc",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "TotpEnabledUtc",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "TotpLastStep",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "TotpPendingProtected",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "TotpPendingUtc",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "TotpSecretProtected",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "TwoFactorFailedCount",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "TwoFactorGraceStartUtc",
                table: "Dynamic_AppUser");

            migrationBuilder.DropColumn(
                name: "TwoFactorLockedUntilUtc",
                table: "Dynamic_AppUser");
        }
    }
}
