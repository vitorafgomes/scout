using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scout.Audit.Storage.Migrations;

/// <inheritdoc />
internal partial class FindingEvidence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "evidence",
            table: "audit_finding",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "evidence",
            table: "audit_finding");
    }
}
