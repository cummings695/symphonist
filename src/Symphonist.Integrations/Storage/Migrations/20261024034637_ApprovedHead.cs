using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphonist.Integrations.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ApprovedHead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedBaseHead",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBranch",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedHead",
                table: "Tickets",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedBaseHead",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ApprovedBranch",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ApprovedHead",
                table: "Tickets");
        }
    }
}
