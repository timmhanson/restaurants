using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupOnboardings",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WelcomeNote = table.Column<string>(type: "TEXT", nullable: true),
                    WelcomeNoteBy = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LaterAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedBy = table.Column<string>(type: "TEXT", nullable: true),
                    StepsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupOnboardings", x => x.GroupId);
                });

            // Groups already at work are not sent back through the welcome: one that has a restaurant counts as set up.
            migrationBuilder.Sql(@"INSERT INTO GroupOnboardings (GroupId, CompletedAt, CompletedBy, StepsJson)
                SELECT g.Id, datetime('now'), 'Already in use', '{}' FROM Groups g
                WHERE EXISTS (SELECT 1 FROM Restaurants r WHERE r.GroupId = g.Id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupOnboardings");
        }
    }
}
