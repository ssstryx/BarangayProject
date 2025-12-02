using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarangayProject.Migrations
{
    /// <inheritdoc />
    public partial class AddSitioIdToAspNetUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SitioId",
                table: "UserProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SitioId",
                table: "AspNetUsers",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_SitioId",
                table: "AspNetUsers",
                column: "SitioId");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Sitios_SitioId",
                table: "AspNetUsers",
                column: "SitioId",
                principalTable: "Sitios",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Sitios_SitioId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_SitioId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SitioId",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "SitioId",
                table: "AspNetUsers");
        }
    }
}
