using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anchor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClassSchoolTagAndCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClassCode",
                table: "Classes",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SchoolTag",
                table: "Classes",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Classes_SchoolTag_ClassCode",
                table: "Classes",
                columns: new[] { "SchoolTag", "ClassCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Classes_SchoolTag_ClassCode",
                table: "Classes");

            migrationBuilder.DropColumn(
                name: "ClassCode",
                table: "Classes");

            migrationBuilder.DropColumn(
                name: "SchoolTag",
                table: "Classes");
        }
    }
}
