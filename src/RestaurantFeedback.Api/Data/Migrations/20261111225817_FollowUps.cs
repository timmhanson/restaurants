using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastActivityAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "RepliedOn",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyMethod",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CommentActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CommentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ContactedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Text = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedByName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByRole = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommentActivities", x => x.Id);
                });

            // Comments already marked responded keep the day they were marked as their reply date.
            migrationBuilder.Sql("UPDATE Comments SET RepliedOn = date(RespondedAt), LastActivityAt = RespondedAt WHERE Responded = 1 AND RespondedAt IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_CommentActivities_CommentId_CreatedAt",
                table: "CommentActivities",
                columns: new[] { "CommentId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommentActivities");

            migrationBuilder.DropColumn(
                name: "LastActivityAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "RepliedOn",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "ReplyMethod",
                table: "Comments");
        }
    }
}
