using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StripeEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StripeEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Deliveries = table.Column<int>(type: "INTEGER", nullable: false),
                    LiveMode = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApiVersion = table.Column<string>(type: "TEXT", nullable: true),
                    ObjectType = table.Column<string>(type: "TEXT", nullable: true),
                    ObjectId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    PaymentId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    InvoiceId = table.Column<int>(type: "INTEGER", nullable: true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    AmountCents = table.Column<int>(type: "INTEGER", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StripeEvents_EventId",
                table: "StripeEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StripeEvents_InvoiceId",
                table: "StripeEvents",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_StripeEvents_ReceivedAt",
                table: "StripeEvents",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StripeEvents");
        }
    }
}
