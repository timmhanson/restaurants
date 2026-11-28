using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiRequestOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Effort",
                table: "AiRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FinalMood",
                table: "AiRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalOffer",
                table: "AiRequests",
                type: "TEXT",
                nullable: true);

            // Requests made before this was recorded: every one ran at low effort, and the ones that became a card
            // take the face and reward that card carries.
            migrationBuilder.Sql("UPDATE AiRequests SET Effort = 'low' WHERE Effort IS NULL;");
            migrationBuilder.Sql(@"UPDATE AiRequests SET
                    FinalMood = (SELECT c.Mood FROM Comments c WHERE c.Id = AiRequests.CommentId),
                    FinalOffer = (SELECT c.CouponOffer FROM Comments c WHERE c.Id = AiRequests.CommentId)
                WHERE CommentId IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Effort",
                table: "AiRequests");

            migrationBuilder.DropColumn(
                name: "FinalMood",
                table: "AiRequests");

            migrationBuilder.DropColumn(
                name: "FinalOffer",
                table: "AiRequests");
        }
    }
}
