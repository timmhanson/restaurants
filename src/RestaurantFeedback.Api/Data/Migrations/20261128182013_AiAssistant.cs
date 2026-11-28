using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AiEnabled",
                table: "GroupBilling",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AiLevel",
                table: "GroupBilling",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AiMonthlyLimitCents",
                table: "GroupBilling",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailBody",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailSubject",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GuestEmail",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Mood",
                table: "Comments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    RestaurantId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    PriceMills = table.Column<int>(type: "INTEGER", nullable: false),
                    WantEmail = table.Column<bool>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CostMicros = table.Column<int>(type: "INTEGER", nullable: false),
                    Input = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    GuestName = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    Mood = table.Column<int>(type: "INTEGER", nullable: true),
                    Offer = table.Column<string>(type: "TEXT", nullable: true),
                    CommentId = table.Column<int>(type: "INTEGER", nullable: true),
                    LinkedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiRequests_CommentId",
                table: "AiRequests",
                column: "CommentId");

            migrationBuilder.CreateIndex(
                name: "IX_AiRequests_GroupId_At",
                table: "AiRequests",
                columns: new[] { "GroupId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_AiRequests_RequestKey",
                table: "AiRequests",
                column: "RequestKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiRequests");

            migrationBuilder.DropColumn(
                name: "AiEnabled",
                table: "GroupBilling");

            migrationBuilder.DropColumn(
                name: "AiLevel",
                table: "GroupBilling");

            migrationBuilder.DropColumn(
                name: "AiMonthlyLimitCents",
                table: "GroupBilling");

            migrationBuilder.DropColumn(
                name: "EmailBody",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "EmailSubject",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "GuestEmail",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "Mood",
                table: "Comments");
        }
    }
}
