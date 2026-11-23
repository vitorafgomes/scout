using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scout.Audit.Storage.Migrations;

/// <inheritdoc />
internal partial class JobEarlier : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "earlier",
            table: "audit_job",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "earlier",
            table: "audit_job");
    }
}
