using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scout.Meetings.Storage.Migrations;

/// <inheritdoc />
internal partial class Initial : Migration
{
    private static readonly string[] StartedUtcTicksNumber = ["started_utc_ticks", "number"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "meetings",
            columns: table => new
            {
                number = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                id = table.Column<string>(type: "TEXT", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                started_utc_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                ended_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                title = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_meetings", x => x.number);
                table.UniqueConstraint("AK_meetings_id", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "schema_version",
            columns: table => new
            {
                version = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_schema_version", x => x.version);
            });

        migrationBuilder.CreateTable(
            name: "keeps",
            columns: table => new
            {
                key = table.Column<string>(type: "TEXT", nullable: false),
                meeting_id = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_keeps", x => x.key);
                table.ForeignKey(
                    name: "FK_keeps_meetings_meeting_id",
                    column: x => x.meeting_id,
                    principalTable: "meetings",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "lines",
            columns: table => new
            {
                meeting_id = table.Column<string>(type: "TEXT", nullable: false),
                seq = table.Column<int>(type: "INTEGER", nullable: false),
                at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                lane = table.Column<string>(type: "TEXT", nullable: false),
                text = table.Column<string>(type: "TEXT", nullable: false),
                translation = table.Column<string>(type: "TEXT", nullable: true),
                language = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_lines", x => new { x.meeting_id, x.seq });
                table.ForeignKey(
                    name: "FK_lines_meetings_meeting_id",
                    column: x => x.meeting_id,
                    principalTable: "meetings",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "turns",
            columns: table => new
            {
                meeting_id = table.Column<string>(type: "TEXT", nullable: false),
                seq = table.Column<int>(type: "INTEGER", nullable: false),
                at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                question = table.Column<string>(type: "TEXT", nullable: false),
                answer = table.Column<string>(type: "TEXT", nullable: false),
                screenshot = table.Column<bool>(type: "INTEGER", nullable: false),
                transcript = table.Column<bool>(type: "INTEGER", nullable: false),
                screen_text = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_turns", x => new { x.meeting_id, x.seq });
                table.ForeignKey(
                    name: "FK_turns_meetings_meeting_id",
                    column: x => x.meeting_id,
                    principalTable: "meetings",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.InsertData(
            table: "schema_version",
            column: "version",
            value: 1);

        migrationBuilder.CreateIndex(
            name: "IX_keeps_meeting_id",
            table: "keeps",
            column: "meeting_id");

        migrationBuilder.CreateIndex(
            name: "IX_meetings_started_utc_ticks_number",
            table: "meetings",
            columns: StartedUtcTicksNumber);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "keeps");

        migrationBuilder.DropTable(
            name: "lines");

        migrationBuilder.DropTable(
            name: "schema_version");

        migrationBuilder.DropTable(
            name: "turns");

        migrationBuilder.DropTable(
            name: "meetings");
    }
}
