using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Service.Storage.Migrations
{
    /// <inheritdoc />
    public partial class FileConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "connections",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "StoredFile",
                table: "connections",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileName",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "StoredFile",
                table: "connections");
        }
    }
}
