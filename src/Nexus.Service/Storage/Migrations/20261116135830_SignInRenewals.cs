using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Service.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SignInRenewals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Renews",
                table: "connect_sessions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Renews",
                table: "connect_sessions");
        }
    }
}
