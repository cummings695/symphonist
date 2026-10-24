using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphonist.Integrations.Storage.Migrations
{
    /// <inheritdoc />
    public partial class MergeAccepted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MergeAcceptedAt",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MergeAcceptedAt",
                table: "Tickets");
        }
    }
}
