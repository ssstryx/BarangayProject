using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarangayProject.Migrations
{
    /// <inheritdoc />
    public partial class AddMiddleNameOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "UserProfiles",
                type: "varchar(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(191)",
                oldMaxLength: 191)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "MiddleName",
                table: "UserProfiles",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "AuditLogs",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(191)",
                oldMaxLength: 191)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Details",
                table: "AuditLogs",
                type: "text",
                maxLength: 191,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldMaxLength: 191)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MiddleName",
                table: "UserProfiles");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "UserProfiles",
                type: "varchar(191)",
                maxLength: 191,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(450)",
                oldMaxLength: 450)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "AuditLogs",
                keyColumn: "UserId",
                keyValue: null,
                column: "UserId",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "AuditLogs",
                type: "varchar(191)",
                maxLength: 191,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(450)",
                oldMaxLength: 450,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "AuditLogs",
                keyColumn: "Details",
                keyValue: null,
                column: "Details",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "Details",
                table: "AuditLogs",
                type: "text",
                maxLength: 191,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldMaxLength: 191,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
