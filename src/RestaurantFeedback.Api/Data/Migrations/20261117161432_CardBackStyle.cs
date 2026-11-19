using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CardBackStyle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackStyle",
                table: "CardDesigns",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "classic");

            migrationBuilder.AddColumn<string>(
                name: "InkColor",
                table: "CardDesigns",
                type: "TEXT",
                maxLength: 9,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalText",
                table: "CardDesigns",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                table: "CardDesigns",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            // The investment groups that already exist are Culver's operators whose printed cards carry culvers.com
            // under the address and in the bottom corner. Give each one a group card with that web address, so their
            // mailed cards match their printed ones from the start. Groups created later choose their own.
            migrationBuilder.Sql("""
                INSERT INTO CardDesigns (GroupId, RestaurantId, Front, Layout, BackStyle, Website, UpdatedAt, UpdatedBy)
                SELECT g.Id, NULL, 'plain', 'centered', 'classic', 'culvers.com', datetime('now'), 'Set-up'
                FROM Groups g
                WHERE NOT EXISTS (SELECT 1 FROM CardDesigns d WHERE d.GroupId = g.Id AND d.RestaurantId IS NULL);
                """);
            migrationBuilder.Sql("UPDATE CardDesigns SET Website = 'culvers.com' WHERE RestaurantId IS NULL AND Website IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackStyle",
                table: "CardDesigns");

            migrationBuilder.DropColumn(
                name: "InkColor",
                table: "CardDesigns");

            migrationBuilder.DropColumn(
                name: "LegalText",
                table: "CardDesigns");

            migrationBuilder.DropColumn(
                name: "Website",
                table: "CardDesigns");
        }
    }
}
