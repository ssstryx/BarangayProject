using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarangayProject.Migrations
{
    /// <inheritdoc />
    public partial class AuditLog_SetNullFK_EntityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_AspNetUsers_ApplicationUserId1",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_ApplicationUserId1",
                table: "AuditLogs");

            migrationBuilder.RenameColumn(
                name: "ApplicationUserId1",
                table: "AuditLogs",
                newName: "EntityId");

            migrationBuilder.AddColumn<string>(
                name: "EntityType",
                table: "AuditLogs",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                table: "AuditLogs",
                type: "text",
                maxLength: 191,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntityType",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Metadata",
                table: "AuditLogs");

            migrationBuilder.RenameColumn(
                name: "EntityId",
                table: "AuditLogs",
                newName: "ApplicationUserId1");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ApplicationUserId1",
                table: "AuditLogs",
                column: "ApplicationUserId1");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_AspNetUsers_ApplicationUserId1",
                table: "AuditLogs",
                column: "ApplicationUserId1",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }
    }
}
