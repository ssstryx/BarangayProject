using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarangayProject.Migrations
{
    /// <inheritdoc />
    public partial class AddSitioBhwManyToMany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.AddColumn<string>(
                name: "ApplicationUserId",
                table: "Sitios",
                type: "varchar(191)",
                maxLength: 191,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SitioBhws",
                columns: table => new
                {
                    SitioId = table.Column<int>(type: "int", nullable: false),
                    BhwId = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AssignedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SitioBhws", x => new { x.SitioId, x.BhwId });
                    table.ForeignKey(
                        name: "FK_SitioBhws_AspNetUsers_BhwId",
                        column: x => x.BhwId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SitioBhws_Sitios_SitioId",
                        column: x => x.SitioId,
                        principalTable: "Sitios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Sitios_ApplicationUserId",
                table: "Sitios",
                column: "ApplicationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SitioBhws_BhwId",
                table: "SitioBhws",
                column: "BhwId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sitios_AspNetUsers_ApplicationUserId",
                table: "Sitios",
                column: "ApplicationUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sitios_AspNetUsers_ApplicationUserId",
                table: "Sitios");

            migrationBuilder.DropTable(
                name: "SitioBhws");

            migrationBuilder.DropIndex(
                name: "IX_Sitios_ApplicationUserId",
                table: "Sitios");

            migrationBuilder.DropColumn(
                name: "ApplicationUserId",
                table: "Sitios");

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
    }
}
