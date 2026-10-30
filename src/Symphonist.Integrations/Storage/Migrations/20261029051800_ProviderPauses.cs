using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphonist.Integrations.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProviderPauses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderPauses",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Since = table.Column<long>(type: "INTEGER", nullable: false),
                    ResetsAt = table.Column<long>(type: "INTEGER", nullable: true),
                    NextProbeAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderPauses", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderPauses");
        }
    }
}
