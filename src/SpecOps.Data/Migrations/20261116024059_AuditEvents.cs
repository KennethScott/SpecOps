using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpecOps.Data.Migrations;

/// <inheritdoc />
public partial class _20261116024059_AuditEvents : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuditEvents",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ActorId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                Actor = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Target = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                Details = table.Column<string>(type: "TEXT", nullable: true),
                ClientIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditEvents", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuditEvents_Action",
            table: "AuditEvents",
            column: "Action");

        migrationBuilder.CreateIndex(
            name: "IX_AuditEvents_TimestampUtc",
            table: "AuditEvents",
            column: "TimestampUtc");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AuditEvents");
    }
}
