using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphonist.Integrations.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RiskReviewer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReviewDue",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Risk",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReview",
                table: "AgentRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "GateDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    StageId = table.Column<string>(type: "TEXT", nullable: false),
                    WaitingSince = table.Column<long>(type: "INTEGER", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", nullable: false),
                    Auto = table.Column<bool>(type: "INTEGER", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GateDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GateReviews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    StageId = table.Column<string>(type: "TEXT", nullable: false),
                    WaitingSince = table.Column<long>(type: "INTEGER", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    Gate = table.Column<string>(type: "TEXT", nullable: false),
                    Risk = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CheckedJson = table.Column<string>(type: "TEXT", nullable: false),
                    AreasJson = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GateReviews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GateDecisions_TicketId_StageId_WaitingSince",
                table: "GateDecisions",
                columns: new[] { "TicketId", "StageId", "WaitingSince" });

            migrationBuilder.CreateIndex(
                name: "IX_GateReviews_TicketId_StageId_WaitingSince",
                table: "GateReviews",
                columns: new[] { "TicketId", "StageId", "WaitingSince" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GateDecisions");

            migrationBuilder.DropTable(
                name: "GateReviews");

            migrationBuilder.DropColumn(
                name: "ReviewDue",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Risk",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "IsReview",
                table: "AgentRuns");
        }
    }
}
