using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpecOps.Data.Migrations;

/// <inheritdoc />
public partial class _20261126231924_Credentials : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Credentials",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ProtectedPassword = table.Column<string>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Credentials", x => x.Id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Credentials");
    }
}
