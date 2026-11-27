using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CardSends : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CardSends",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CommentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ByName = table.Column<string>(type: "TEXT", nullable: true),
                    Method = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Resend = table.Column<bool>(type: "INTEGER", nullable: false),
                    StreetAddress = table.Column<string>(type: "TEXT", nullable: true),
                    City = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: true),
                    Zip = table.Column<string>(type: "TEXT", nullable: true),
                    AddressStatus = table.Column<string>(type: "TEXT", nullable: true),
                    MailId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    MailStatus = table.Column<string>(type: "TEXT", nullable: true),
                    StatusCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Test = table.Column<bool>(type: "INTEGER", nullable: false),
                    CostCents = table.Column<int>(type: "INTEGER", nullable: false),
                    FeeCents = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardSends", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardSends_Comments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "Comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardSends_CommentId",
                table: "CardSends",
                column: "CommentId");

            migrationBuilder.CreateIndex(
                name: "IX_CardSends_MailId",
                table: "CardSends",
                column: "MailId");

            // What has already been done is carried over, so every card's history starts complete.
            const string columns = "CommentId, Number, At, ByUserId, ByName, Method, Outcome, Resend, StreetAddress, City, State, Zip, AddressStatus, MailId, MailStatus, Test, CostCents, FeeCents, Error";
            // 1. Cards printed and mailed for the restaurant: the send each card carries.
            migrationBuilder.Sql($@"INSERT INTO CardSends ({columns})
                SELECT Id, 0, MailedAt, NULL, MailedByName, 'mail', CASE WHEN MailStatus = 'cancelled' THEN 'cancelled' ELSE 'sent' END, 0,
                       StreetAddress, City, State, Zip, AddressStatus, MailId, MailStatus, MailTest, COALESCE(MailEstimateCents, 0), COALESCE(MailFeeCents, 0), NULL
                FROM Comments WHERE MailId IS NOT NULL AND MailedAt IS NOT NULL;");
            // 2. Cards printed in the restaurant and marked as sent.
            migrationBuilder.Sql($@"INSERT INTO CardSends ({columns})
                SELECT Id, 0, COALESCE(RespondedAt, PrintedAt), RespondedByUserId, RespondedByName, 'stamp', 'sent', 0,
                       StreetAddress, City, State, Zip, AddressStatus, NULL, NULL, 0, 0, 0, NULL
                FROM Comments WHERE MailId IS NULL AND Responded = 1 AND PrintedAt IS NOT NULL;");
            // 3. Attempts the mailing service refused, from the audit log. The address used then was not recorded.
            migrationBuilder.Sql($@"INSERT INTO CardSends ({columns})
                SELECT CAST(a.EntityId AS INTEGER), 0, a.Timestamp, a.UserId, a.UserName, 'mail', 'refused', 0,
                       NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, 0, 0, CASE WHEN json_valid(a.Details) THEN json_extract(a.Details, '$.error') ELSE NULL END
                FROM AuditLogs a
                WHERE a.Action = 'CardMailFailed' AND a.EntityType = 'Comment' AND CAST(a.EntityId AS INTEGER) IN (SELECT Id FROM Comments);");
            // 4. Each card's attempts are numbered in the order they were made.
            migrationBuilder.Sql(@"UPDATE CardSends SET Number =
                (SELECT COUNT(*) FROM CardSends s WHERE s.CommentId = CardSends.CommentId AND (s.At < CardSends.At OR (s.At = CardSends.At AND s.Id <= CardSends.Id)));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardSends");
        }
    }
}
