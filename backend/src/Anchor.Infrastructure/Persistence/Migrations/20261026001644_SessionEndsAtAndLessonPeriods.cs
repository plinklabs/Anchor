using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anchor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionEndsAtAndLessonPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndsAt",
                table: "Sessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LessonPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Start = table.Column<TimeOnly>(type: "time", nullable: false),
                    End = table.Column<TimeOnly>(type: "time", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LessonPeriods_Schools_SchoolId",
                        column: x => x.SchoolId,
                        principalTable: "Schools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LessonPeriods_SchoolId",
                table: "LessonPeriods",
                column: "SchoolId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LessonPeriods");

            migrationBuilder.DropColumn(
                name: "EndsAt",
                table: "Sessions");
        }
    }
}
