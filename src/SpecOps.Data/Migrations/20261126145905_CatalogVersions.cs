using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpecOps.Data.Migrations;

/// <inheritdoc />
public partial class _20261126145905_CatalogVersions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CatalogVersions",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedById = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Summary = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                Json = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogVersions", x => x.Id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CatalogVersions");
    }
}
