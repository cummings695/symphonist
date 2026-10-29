using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphonist.Integrations.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Touches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Touches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    StageId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Touches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Touches_TicketId",
                table: "Touches",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Touches");
        }
    }
}
