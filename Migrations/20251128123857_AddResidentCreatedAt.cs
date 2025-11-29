using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarangayProject.Migrations
{
    /// <inheritdoc />
    public partial class AddResidentCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Residents",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Residents");
        }
    }
}
