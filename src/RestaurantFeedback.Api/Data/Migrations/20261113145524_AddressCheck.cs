using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddressCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AddressCheckedAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressStatus",
                table: "Comments",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddressCheckedAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "AddressStatus",
                table: "Comments");
        }
    }
}
