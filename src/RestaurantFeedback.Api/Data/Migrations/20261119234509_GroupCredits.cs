using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreditCents",
                table: "MailInvoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "RequirePayMethod",
                table: "GroupBilling",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GroupCredits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    AmountCents = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    InvoiceId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupCredits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupCredits_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupCredits_GroupId",
                table: "GroupCredits",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupCredits_Key",
                table: "GroupCredits",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupCredits");

            migrationBuilder.DropColumn(
                name: "CreditCents",
                table: "MailInvoices");

            migrationBuilder.DropColumn(
                name: "RequirePayMethod",
                table: "GroupBilling");
        }
    }
}
