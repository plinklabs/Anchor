using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anchor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionEventSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SessionEventSummaries",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    FirstAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionEventSummaries", x => new { x.SessionId, x.UserId, x.Kind });
                    table.ForeignKey(
                        name: "FK_SessionEventSummaries_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SessionEventSummaries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionEventSummaries_UserId",
                table: "SessionEventSummaries",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionEventSummaries");
        }
    }
}
