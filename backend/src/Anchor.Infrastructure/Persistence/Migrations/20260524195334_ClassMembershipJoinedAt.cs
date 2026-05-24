using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anchor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClassMembershipJoinedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "JoinedAt",
                table: "ClassMemberships",
                type: "datetimeoffset",
                nullable: false,
                defaultValueSql: "SYSDATETIMEOFFSET()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JoinedAt",
                table: "ClassMemberships");
        }
    }
}
