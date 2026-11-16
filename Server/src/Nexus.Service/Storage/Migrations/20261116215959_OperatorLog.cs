using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Service.Storage.Migrations
{
    /// <inheritdoc />
    public partial class OperatorLog : Migration
    {
        private static readonly string[] WorkspaceThenId = ["WorkspaceId", "Id"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "log_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    At = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Level = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Text = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Actor = table.Column<string>(type: "varchar(204)", maxLength: 204, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    WorkspaceId = table.Column<int>(type: "int", nullable: true),
                    ConnectionId = table.Column<int>(type: "int", nullable: true),
                    SyncId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_log_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_log_entries_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_log_entries_syncs_SyncId",
                        column: x => x.SyncId,
                        principalTable: "syncs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_log_entries_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_log_entries_At",
                table: "log_entries",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_log_entries_ConnectionId",
                table: "log_entries",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_log_entries_SyncId",
                table: "log_entries",
                column: "SyncId");

            migrationBuilder.CreateIndex(
                name: "IX_log_entries_WorkspaceId_Id",
                table: "log_entries",
                columns: WorkspaceThenId);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "log_entries");
        }
    }
}
