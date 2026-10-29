using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphonist.Integrations.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "Work"); // every ticket before release batches is a work ticket

            migrationBuilder.AddColumn<string>(
                name: "ReleaseSummary",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReleaseTicketId",
                table: "Tickets",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ReleaseSummary",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ReleaseTicketId",
                table: "Tickets");
        }
    }
}
