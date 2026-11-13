using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupPostageAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PostageKeyEncrypted",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostageKeyEnding",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PostageKeyIsTest",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PostageKeySetAt",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostageKeySetBy",
                table: "Groups",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PostageKeyEncrypted",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PostageKeyEnding",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PostageKeyIsTest",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PostageKeySetAt",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PostageKeySetBy",
                table: "Groups");
        }
    }
}
