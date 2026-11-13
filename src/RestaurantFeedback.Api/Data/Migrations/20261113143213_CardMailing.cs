using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CardMailing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FollowUpDays",
                table: "Restaurants",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FollowedUpAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FollowedUpByName",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FollowedUpOn",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostageByName",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PostageCents",
                table: "Comments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostageId",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostageLabelUrl",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostageProvider",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PostagePurchasedAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PostageTest",
                table: "Comments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PostageTrackingCode",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrintCount",
                table: "Comments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PrintedAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrintedByName",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RedeemedAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RedeemedByName",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "RedeemedOn",
                table: "Comments",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FollowUpDays",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "FollowedUpAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "FollowedUpByName",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "FollowedUpOn",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PostageByName",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PostageCents",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PostageId",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PostageLabelUrl",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PostageProvider",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PostagePurchasedAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PostageTest",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PostageTrackingCode",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PrintCount",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PrintedAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PrintedByName",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "RedeemedAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "RedeemedByName",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "RedeemedOn",
                table: "Comments");
        }
    }
}
