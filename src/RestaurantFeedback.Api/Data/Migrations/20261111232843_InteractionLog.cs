using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InteractionLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InteractionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Role = table.Column<int>(type: "INTEGER", nullable: true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    RestaurantId = table.Column<int>(type: "INTEGER", nullable: true),
                    ViewingAs = table.Column<bool>(type: "INTEGER", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InteractionLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InteractionLogs_At",
                table: "InteractionLogs",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_InteractionLogs_GroupId_At",
                table: "InteractionLogs",
                columns: new[] { "GroupId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_InteractionLogs_RestaurantId_At",
                table: "InteractionLogs",
                columns: new[] { "RestaurantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_InteractionLogs_UserId_At",
                table: "InteractionLogs",
                columns: new[] { "UserId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InteractionLogs");
        }
    }
}
