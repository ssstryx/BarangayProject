using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarangayProject.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignedBhwToSitio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedBhwId",
                table: "Sitios",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Sitios_AssignedBhwId",
                table: "Sitios",
                column: "AssignedBhwId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sitios_AspNetUsers_AssignedBhwId",
                table: "Sitios",
                column: "AssignedBhwId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sitios_AspNetUsers_AssignedBhwId",
                table: "Sitios");

            migrationBuilder.DropIndex(
                name: "IX_Sitios_AssignedBhwId",
                table: "Sitios");

            migrationBuilder.DropColumn(
                name: "AssignedBhwId",
                table: "Sitios");
        }
    }
}
