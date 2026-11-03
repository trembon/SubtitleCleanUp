using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubtitleCleanUp.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContentCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CleanedSha256",
                table: "FileOperations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CleanedSha256",
                table: "FileOperations");
        }
    }
}
