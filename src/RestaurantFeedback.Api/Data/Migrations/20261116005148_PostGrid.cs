using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PostGrid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PostGridKeyEncrypted",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostGridKeyEnding",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PostGridKeyIsTest",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PostGridKeySetAt",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostGridKeySetBy",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MailAttempts",
                table: "Comments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MailEstimateCents",
                table: "Comments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MailId",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MailProvider",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MailStatus",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MailTest",
                table: "Comments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "MailedAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MailedByName",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CardArtworks",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    WidthPx = table.Column<int>(type: "INTEGER", nullable: true),
                    HeightPx = table.Column<int>(type: "INTEGER", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UploadedBy = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardArtworks", x => x.GroupId);
                    table.ForeignKey(
                        name: "FK_CardArtworks_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_MailId",
                table: "Comments",
                column: "MailId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardArtworks");

            migrationBuilder.DropIndex(
                name: "IX_Comments_MailId",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "PostGridKeyEncrypted",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PostGridKeyEnding",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PostGridKeyIsTest",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PostGridKeySetAt",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PostGridKeySetBy",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "MailAttempts",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "MailEstimateCents",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "MailId",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "MailProvider",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "MailStatus",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "MailTest",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "MailedAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "MailedByName",
                table: "Comments");
        }
    }
}
