using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PermissionLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ManagerNo",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ManagerYes",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OwnerNo",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SiteManagerNo",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SiteManagerYes",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SiteOwnerNo",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SiteOwnerYes",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GroupDecides",
                table: "GroupBilling",
                type: "INTEGER",
                nullable: false,
                defaultValue: 14);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManagerNo",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "ManagerYes",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "OwnerNo",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "SiteManagerNo",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "SiteManagerYes",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "SiteOwnerNo",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "SiteOwnerYes",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "GroupDecides",
                table: "GroupBilling");
        }
    }
}
