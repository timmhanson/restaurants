using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CardGuid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "Comments",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Cards entered before this get a random GUID each (version 4 layout, in the upper-case text form the
            // database keeps GUIDs in), so the unique index below can be created. New cards get theirs from the app.
            migrationBuilder.Sql("""
                UPDATE Comments SET PublicId =
                    hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-' ||
                    substr('89AB', 1 + (abs(random()) % 4), 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Comments_PublicId",
                table: "Comments",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Comments_PublicId",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "Comments");
        }
    }
}
