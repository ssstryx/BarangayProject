using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarangayProject.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdSitio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Location",
                table: "Sitios");

            migrationBuilder.AlterColumn<string>(
                name: "Details",
                table: "Households",
                type: "text",
                maxLength: 191,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(191)",
                oldMaxLength: 191,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "ArchivedBy",
                table: "Households",
                type: "varchar(191)",
                maxLength: 191,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(191)",
                oldMaxLength: 191)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "SitioId",
                table: "Households",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Households_SitioId",
                table: "Households",
                column: "SitioId");

            migrationBuilder.AddForeignKey(
                name: "FK_Households_Sitios_SitioId",
                table: "Households",
                column: "SitioId",
                principalTable: "Sitios",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Households_Sitios_SitioId",
                table: "Households");

            migrationBuilder.DropIndex(
                name: "IX_Households_SitioId",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "SitioId",
                table: "Households");

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "Sitios",
                type: "varchar(250)",
                maxLength: 250,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Details",
                table: "Households",
                type: "varchar(191)",
                maxLength: 191,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldMaxLength: 191,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "Households",
                keyColumn: "ArchivedBy",
                keyValue: null,
                column: "ArchivedBy",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "ArchivedBy",
                table: "Households",
                type: "varchar(191)",
                maxLength: 191,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(191)",
                oldMaxLength: 191,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
