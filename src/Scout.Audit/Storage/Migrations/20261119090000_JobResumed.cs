using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scout.Audit.Storage.Migrations;

/// <inheritdoc />
internal partial class JobResumed : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "resumed",
            table: "audit_job",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "resumed",
            table: "audit_job");
    }
}
