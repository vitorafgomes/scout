using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scout.Audit.Storage.Migrations;

/// <inheritdoc />
internal partial class BatchLeftOut : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "left_out",
            table: "audit_batch",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "left_out",
            table: "audit_batch");
    }
}
