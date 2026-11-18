using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Service.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SignInLives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignInMail",
                table: "workspaces",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Off")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Accounts",
                table: "connections",
                type: "varchar(4000)",
                maxLength: 4000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "KeptAliveAt",
                table: "connections",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignInEndedAt",
                table: "connections",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignInEndsAt",
                table: "connections",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignInWarnedAt",
                table: "connections",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedInAt",
                table: "connections",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sign_in_mail_people",
                columns: table => new
                {
                    WorkspaceId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sign_in_mail_people", x => new { x.WorkspaceId, x.UserId });
                    table.ForeignKey(
                        name: "FK_sign_in_mail_people_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sign_in_mail_people_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_sign_in_mail_people_UserId",
                table: "sign_in_mail_people",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sign_in_mail_people");

            migrationBuilder.DropColumn(
                name: "SignInMail",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "Accounts",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "KeptAliveAt",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "SignInEndedAt",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "SignInEndsAt",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "SignInWarnedAt",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "SignedInAt",
                table: "connections");
        }
    }
}
