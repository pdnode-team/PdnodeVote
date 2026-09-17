using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PdnodeVote.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCategorySlugAndPollFeedIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Polls_Status_IsPinned_CreatedAt",
                table: "Polls",
                columns: new[] { "Status", "IsPinned", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Slug",
                table: "Categories",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Polls_Status_IsPinned_CreatedAt",
                table: "Polls");

            migrationBuilder.DropIndex(
                name: "IX_Categories_Slug",
                table: "Categories");
        }
    }
}
