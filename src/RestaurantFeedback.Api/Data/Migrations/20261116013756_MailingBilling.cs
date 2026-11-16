using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MailingBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardArtworks");

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

            migrationBuilder.AddColumn<int>(
                name: "MailFeeCents",
                table: "Comments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CardDesigns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    RestaurantId = table.Column<int>(type: "INTEGER", nullable: true),
                    Front = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Layout = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Headline = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    BackMessage = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BackgroundColor = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                    TextColor = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardDesigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardDesigns_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CardDesigns_Restaurants_RestaurantId",
                        column: x => x.RestaurantId,
                        principalTable: "Restaurants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupBilling",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    FeeCents = table.Column<int>(type: "INTEGER", nullable: true),
                    MonthlyLimitCents = table.Column<int>(type: "INTEGER", nullable: true),
                    BillingName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BillingEmail = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Street = table.Column<string>(type: "TEXT", nullable: true),
                    City = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: true),
                    Zip = table.Column<string>(type: "TEXT", nullable: true),
                    PayCustomerId = table.Column<string>(type: "TEXT", nullable: true),
                    PayMethodId = table.Column<string>(type: "TEXT", nullable: true),
                    PayKind = table.Column<string>(type: "TEXT", nullable: true),
                    PayFunding = table.Column<string>(type: "TEXT", nullable: true),
                    PayLabel = table.Column<string>(type: "TEXT", nullable: true),
                    PayMandateId = table.Column<string>(type: "TEXT", nullable: true),
                    PaySetAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaySetBy = table.Column<string>(type: "TEXT", nullable: true),
                    AutoPay = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupBilling", x => x.GroupId);
                    table.ForeignKey(
                        name: "FK_GroupBilling_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailInvoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Cards = table.Column<int>(type: "INTEGER", nullable: false),
                    CostCents = table.Column<int>(type: "INTEGER", nullable: false),
                    FeeCents = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalCents = table.Column<int>(type: "INTEGER", nullable: false),
                    LinesJson = table.Column<string>(type: "TEXT", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaidHow = table.Column<string>(type: "TEXT", nullable: true),
                    PaidNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PaidBy = table.Column<string>(type: "TEXT", nullable: true),
                    SurchargeCents = table.Column<int>(type: "INTEGER", nullable: false),
                    PaymentRef = table.Column<string>(type: "TEXT", nullable: true),
                    PaymentAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    PaymentError = table.Column<string>(type: "TEXT", nullable: true),
                    EmailedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EmailedTo = table.Column<string>(type: "TEXT", nullable: true),
                    PrintedId = table.Column<string>(type: "TEXT", nullable: true),
                    PrintedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeliveryError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailInvoices_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CardImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DesignId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
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
                    table.PrimaryKey("PK_CardImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardImages_CardDesigns_DesignId",
                        column: x => x.DesignId,
                        principalTable: "CardDesigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardDesigns_GroupId_RestaurantId",
                table: "CardDesigns",
                columns: new[] { "GroupId", "RestaurantId" });

            migrationBuilder.CreateIndex(
                name: "IX_CardDesigns_RestaurantId",
                table: "CardDesigns",
                column: "RestaurantId");

            migrationBuilder.CreateIndex(
                name: "IX_CardImages_DesignId",
                table: "CardImages",
                column: "DesignId");

            migrationBuilder.CreateIndex(
                name: "IX_CardImages_GroupId_Hash",
                table: "CardImages",
                columns: new[] { "GroupId", "Hash" });

            migrationBuilder.CreateIndex(
                name: "IX_MailInvoices_GroupId_Month",
                table: "MailInvoices",
                columns: new[] { "GroupId", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailInvoices_Status",
                table: "MailInvoices",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardImages");

            migrationBuilder.DropTable(
                name: "GroupBilling");

            migrationBuilder.DropTable(
                name: "MailInvoices");

            migrationBuilder.DropTable(
                name: "CardDesigns");

            migrationBuilder.DropColumn(
                name: "MailFeeCents",
                table: "Comments");

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

            migrationBuilder.CreateTable(
                name: "CardArtworks",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    HeightPx = table.Column<int>(type: "INTEGER", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UploadedBy = table.Column<string>(type: "TEXT", nullable: false),
                    WidthPx = table.Column<int>(type: "INTEGER", nullable: true)
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
        }
    }
}
