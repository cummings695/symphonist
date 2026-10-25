using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphonist.Integrations.Storage.Migrations
{
    /// <inheritdoc />
    public partial class EvidenceAndPrComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EvidenceDue",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "PrCommentsHandledAt",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrFeedbackJson",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsEvidence",
                table: "AgentRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Evidence",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    StageId = table.Column<string>(type: "TEXT", nullable: false),
                    WaitingSince = table.Column<long>(type: "INTEGER", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Head = table.Column<string>(type: "TEXT", nullable: true),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: true),
                    HasIssues = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ScreenshotsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Posted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Evidence", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_TicketId",
                table: "Evidence",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Evidence");

            migrationBuilder.DropColumn(
                name: "EvidenceDue",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "PrCommentsHandledAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "PrFeedbackJson",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "IsEvidence",
                table: "AgentRuns");
        }
    }
}
