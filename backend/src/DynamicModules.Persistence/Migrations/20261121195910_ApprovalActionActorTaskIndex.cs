using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalActionActorTaskIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalAction_ActorUserId_TaskId",
                table: "Dynamic_ApprovalAction",
                columns: new[] { "ActorUserId", "TaskId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Dynamic_ApprovalAction_ActorUserId_TaskId",
                table: "Dynamic_ApprovalAction");
        }
    }
}
