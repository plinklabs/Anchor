using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anchor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BundleIsArchived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "Bundles",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "Bundles");
        }
    }
}
