using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anchor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropSessionMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Sessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "Sessions",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }
    }
}
