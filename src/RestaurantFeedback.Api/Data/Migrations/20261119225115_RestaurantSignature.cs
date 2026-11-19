using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestaurantSignature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignatureName",
                table: "Restaurants",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignatureName",
                table: "Restaurants");
        }
    }
}
