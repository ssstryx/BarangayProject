using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarangayProject.Migrations
{
    /// <inheritdoc />
    public partial class AuditLog_SetNullFK : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_AspNetUsers_ApplicationUserId",
                table: "AuditLogs");

            migrationBuilder.AddColumn<string>(
                name: "ApplicationUserId1",
                table: "AuditLogs",
                type: "varchar(191)",
                maxLength: 191,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ApplicationUserId1",
                table: "AuditLogs",
                column: "ApplicationUserId1");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_AspNetUsers_ApplicationUserId",
                table: "AuditLogs",
                column: "ApplicationUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_AspNetUsers_ApplicationUserId1",
                table: "AuditLogs",
                column: "ApplicationUserId1",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_AspNetUsers_ApplicationUserId",
                table: "AuditLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_AspNetUsers_ApplicationUserId1",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_ApplicationUserId1",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "ApplicationUserId1",
                table: "AuditLogs");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_AspNetUsers_ApplicationUserId",
                table: "AuditLogs",
                column: "ApplicationUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }
    }
}
