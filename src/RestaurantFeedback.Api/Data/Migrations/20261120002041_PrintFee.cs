using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrintFee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PrintFeeAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrintFeeCents",
                table: "Comments",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrintFeeAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PrintFeeCents",
                table: "Comments");
        }
    }
}
