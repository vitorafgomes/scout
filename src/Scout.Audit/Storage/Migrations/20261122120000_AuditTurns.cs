using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scout.Audit.Storage.Migrations;

/// <inheritdoc />
internal partial class AuditTurns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "scout_version",
            table: "audit_job",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "chat_model",
            table: "audit_job",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "audit_turn",
            columns: table => new
            {
                number = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                job_id = table.Column<string>(type: "TEXT", nullable: false),
                batch = table.Column<int>(type: "INTEGER", nullable: true),
                reading = table.Column<string>(type: "TEXT", nullable: false),
                contest_group = table.Column<int>(type: "INTEGER", nullable: true),
                attempt = table.Column<int>(type: "INTEGER", nullable: false),
                extra_request = table.Column<bool>(type: "INTEGER", nullable: false),
                started = table.Column<DateTime>(type: "TEXT", nullable: false),
                elapsed_ms = table.Column<long>(type: "INTEGER", nullable: false),
                request_length = table.Column<int>(type: "INTEGER", nullable: false),
                calls = table.Column<string>(type: "TEXT", nullable: false),
                presented = table.Column<int>(type: "INTEGER", nullable: false),
                placed = table.Column<int>(type: "INTEGER", nullable: false),
                left_out = table.Column<int>(type: "INTEGER", nullable: false),
                failure = table.Column<string>(type: "TEXT", nullable: true),
                held = table.Column<int>(type: "INTEGER", nullable: true),
                refuted = table.Column<int>(type: "INTEGER", nullable: true),
                uncertain = table.Column<int>(type: "INTEGER", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_turn", x => x.number);
                table.ForeignKey(
                    name: "FK_audit_turn_audit_job_job_id",
                    column: x => x.job_id,
                    principalTable: "audit_job",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_audit_turn_job_id",
            table: "audit_turn",
            column: "job_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_turn");

        migrationBuilder.DropColumn(
            name: "scout_version",
            table: "audit_job");

        migrationBuilder.DropColumn(
            name: "chat_model",
            table: "audit_job");
    }
}
