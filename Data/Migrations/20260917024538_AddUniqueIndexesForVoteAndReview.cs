using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PdnodeVote.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueIndexesForVoteAndReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VoteRecords_PollId_UserId",
                table: "VoteRecords");

            migrationBuilder.DropIndex(
                name: "IX_CategoryRequestReviews_RequestId",
                table: "CategoryRequestReviews");

            migrationBuilder.CreateIndex(
                name: "IX_VoteRecords_PollId_UserId",
                table: "VoteRecords",
                columns: new[] { "PollId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CategoryRequestReviews_RequestId_ReviewerId",
                table: "CategoryRequestReviews",
                columns: new[] { "RequestId", "ReviewerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VoteRecords_PollId_UserId",
                table: "VoteRecords");

            migrationBuilder.DropIndex(
                name: "IX_CategoryRequestReviews_RequestId_ReviewerId",
                table: "CategoryRequestReviews");

            migrationBuilder.CreateIndex(
                name: "IX_VoteRecords_PollId_UserId",
                table: "VoteRecords",
                columns: new[] { "PollId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryRequestReviews_RequestId",
                table: "CategoryRequestReviews",
                column: "RequestId");
        }
    }
}
