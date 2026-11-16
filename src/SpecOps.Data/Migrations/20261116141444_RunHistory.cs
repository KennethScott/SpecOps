using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpecOps.Data.Migrations;

/// <inheritdoc />
public partial class _20261116141444_RunHistory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Runs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ScriptId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ScriptName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ClientIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                Code = table.Column<string>(type: "TEXT", nullable: true),
                QueuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                ErrorCount = table.Column<int>(type: "INTEGER", nullable: false),
                WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                ResultCount = table.Column<int>(type: "INTEGER", nullable: false),
                ResultsTruncated = table.Column<bool>(type: "INTEGER", nullable: false),
                OutputJson = table.Column<string>(type: "TEXT", nullable: true),
                ResultsJson = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Runs", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Runs_EndedAtUtc",
            table: "Runs",
            column: "EndedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_Runs_QueuedAtUtc",
            table: "Runs",
            column: "QueuedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_Runs_UserId_QueuedAtUtc",
            table: "Runs",
            columns: new[] { "UserId", "QueuedAtUtc" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Runs");
    }
}
