using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BillSplit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MailInvoices_GroupId_Month",
                table: "MailInvoices");

            migrationBuilder.AddColumn<string>(
                name: "BillSet",
                table: "Restaurants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "MailInvoices",
                type: "TEXT",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UnitName",
                table: "MailInvoices",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillSplit",
                table: "GroupBilling",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_MailInvoices_GroupId_Month_Unit",
                table: "MailInvoices",
                columns: new[] { "GroupId", "Month", "Unit" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MailInvoices_GroupId_Month_Unit",
                table: "MailInvoices");

            migrationBuilder.DropColumn(
                name: "BillSet",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "MailInvoices");

            migrationBuilder.DropColumn(
                name: "UnitName",
                table: "MailInvoices");

            migrationBuilder.DropColumn(
                name: "BillSplit",
                table: "GroupBilling");

            migrationBuilder.CreateIndex(
                name: "IX_MailInvoices_GroupId_Month",
                table: "MailInvoices",
                columns: new[] { "GroupId", "Month" },
                unique: true);
        }
    }
}
