using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WelcomeTour : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "WelcomedAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            // People already using the site are not sent on a tour of it: only those who have never signed in get one.
            migrationBuilder.Sql("UPDATE Users SET WelcomedAt = datetime('now') WHERE LastLoginAt IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WelcomedAt",
                table: "Users");
        }
    }
}
