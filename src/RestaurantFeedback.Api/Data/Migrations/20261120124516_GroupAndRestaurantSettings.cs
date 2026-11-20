using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupAndRestaurantSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MailLive",
                table: "Restaurants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MailOn",
                table: "Restaurants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MailMode",
                table: "GroupBilling",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MailPerRestaurant",
                table: "GroupBilling",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SignatureForAll",
                table: "GroupBilling",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UsualWay",
                table: "GroupBilling",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MailLive",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "MailOn",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "MailMode",
                table: "GroupBilling");

            migrationBuilder.DropColumn(
                name: "MailPerRestaurant",
                table: "GroupBilling");

            migrationBuilder.DropColumn(
                name: "SignatureForAll",
                table: "GroupBilling");

            migrationBuilder.DropColumn(
                name: "UsualWay",
                table: "GroupBilling");
        }
    }
}
