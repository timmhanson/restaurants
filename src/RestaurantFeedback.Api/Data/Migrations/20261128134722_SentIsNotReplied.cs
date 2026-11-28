using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RestaurantFeedback.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SentIsNotReplied : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "InMailAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SentByName",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InMailAt",
                table: "CardSends",
                type: "TEXT",
                nullable: true);

            // Until now, mailing a card (or printing one and marking it as sent) also marked the guest as replied to.
            // The two are told apart from here on, and what has already been done is carried over without losing any
            // of it. A reply recorded by the site itself is known by the words the site wrote in its history entry
            // (ActivityKind.Replied = 2); a reply a person recorded is left exactly as it is.
            const string byTheSite = "(a.Text LIKE 'Coupon card printed and mailed%' OR a.Text LIKE 'Coupon card mailed by %')";
            const string repliedByTheSite = @"Comments.Responded = 1 AND EXISTS (SELECT 1 FROM CommentActivities a WHERE a.CommentId = Comments.Id AND a.Kind = 2
                    AND a.Id = (SELECT MAX(b.Id) FROM CommentActivities b WHERE b.CommentId = Comments.Id AND b.Kind = 2) AND " + byTheSite + ")";
            const string hasGone = "EXISTS (SELECT 1 FROM CardSends s WHERE s.CommentId = Comments.Id AND s.Outcome = 'sent')";

            // 1. A card the site marked as replied but whose history holds no send that went (printed and marked as
            //    sent after a cancelled mailing): the send is added to its history, so it stays sent.
            migrationBuilder.Sql($@"INSERT INTO CardSends (CommentId, Number, At, ByUserId, ByName, Method, Outcome, Resend, StreetAddress, City, State, Zip, AddressStatus, MailId, MailStatus, Test, CostCents, FeeCents, Error)
                SELECT Id, (SELECT COALESCE(MAX(s.Number), 0) + 1 FROM CardSends s WHERE s.CommentId = Comments.Id), COALESCE(RespondedAt, PrintedAt, CreatedAt), RespondedByUserId, RespondedByName,
                       'stamp', 'sent', 0, StreetAddress, City, State, Zip, AddressStatus, NULL, NULL, 0, 0, 0, NULL
                FROM Comments WHERE {repliedByTheSite} AND NOT {hasGone};");
            // 2. Every card that has gone is marked as sent, by its latest send.
            migrationBuilder.Sql($@"UPDATE Comments SET
                    SentAt = (SELECT s.At FROM CardSends s WHERE s.CommentId = Comments.Id AND s.Outcome = 'sent' ORDER BY s.Number DESC, s.Id DESC LIMIT 1),
                    SentByName = (SELECT s.ByName FROM CardSends s WHERE s.CommentId = Comments.Id AND s.Outcome = 'sent' ORDER BY s.Number DESC, s.Id DESC LIMIT 1)
                WHERE {hasGone};");
            // 3. Cards the site marked as replied are waiting for a person's reply again.
            migrationBuilder.Sql($@"UPDATE Comments SET Responded = 0, RespondedAt = NULL, RespondedByUserId = NULL, RespondedByName = NULL, ReplyMethod = NULL, RepliedOn = NULL
                WHERE {repliedByTheSite};");
            // 4. The site's own "reply confirmed" entries stay in each card's history, as what they were: the card
            //    being sent (ActivityKind.Sent = 12). The older wording named the mail house, which groups never see.
            migrationBuilder.Sql($@"UPDATE CommentActivities AS a SET Kind = 12,
                    Text = CASE WHEN a.Text LIKE 'Coupon card mailed by %' THEN 'Coupon card printed and mailed for the restaurant.' ELSE a.Text END
                WHERE a.Kind = 2 AND {byTheSite};");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InMailAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "SentAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "SentByName",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "InMailAt",
                table: "CardSends");
        }
    }
}
