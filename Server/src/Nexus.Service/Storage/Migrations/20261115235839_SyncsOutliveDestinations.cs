using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Service.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SyncsOutliveDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_syncs_destinations_DestinationId",
                table: "syncs");

            migrationBuilder.AlterColumn<int>(
                name: "DestinationId",
                table: "syncs",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<DateTime>(
                name: "KeyCheckedAt",
                table: "destinations",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyName",
                table: "destinations",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "KeyPrefix",
                table: "destinations",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "KeyProblem",
                table: "destinations",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddForeignKey(
                name: "FK_syncs_destinations_DestinationId",
                table: "syncs",
                column: "DestinationId",
                principalTable: "destinations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_syncs_destinations_DestinationId",
                table: "syncs");

            migrationBuilder.DropColumn(
                name: "KeyCheckedAt",
                table: "destinations");

            migrationBuilder.DropColumn(
                name: "KeyName",
                table: "destinations");

            migrationBuilder.DropColumn(
                name: "KeyPrefix",
                table: "destinations");

            migrationBuilder.DropColumn(
                name: "KeyProblem",
                table: "destinations");

            migrationBuilder.AlterColumn<int>(
                name: "DestinationId",
                table: "syncs",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_syncs_destinations_DestinationId",
                table: "syncs",
                column: "DestinationId",
                principalTable: "destinations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
