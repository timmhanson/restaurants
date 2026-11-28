using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiCardDesign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AiDesignTries",
                table: "GroupBilling",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "AiRequests",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Every request made so far was the assistant on Enter Card.
            migrationBuilder.Sql("UPDATE AiRequests SET Kind = 'card' WHERE Kind IS NULL OR Kind = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiDesignTries",
                table: "GroupBilling");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "AiRequests");
        }
    }
}
