using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarangayProject.Migrations
{
    /// <inheritdoc />
    public partial class FixAuditLogsRemoveSitioFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1️⃣ Drop old foreign key first
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_AspNetUsers_ApplicationUserId",
                table: "AuditLogs");

            // 2️⃣ Drop index if it exists
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_ApplicationUserId",
                table: "AuditLogs");

            // 3️⃣ Modify the column
            migrationBuilder.AlterColumn<string>(
                name: "ApplicationUserId",
                table: "AuditLogs",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(191)",
                oldMaxLength: 191,
                oldNullable: true);

            // 4️⃣ Recreate index
            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ApplicationUserId",
                table: "AuditLogs",
                column: "ApplicationUserId");

            // 5️⃣ Re-add foreign key with safe ON DELETE SET NULL
            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_AspNetUsers_ApplicationUserId",
                table: "AuditLogs",
                column: "ApplicationUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }


        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ApplicationUserId",
                table: "AuditLogs",
                type: "varchar(191)",
                maxLength: 191,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(450)",
                oldMaxLength: 450,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
