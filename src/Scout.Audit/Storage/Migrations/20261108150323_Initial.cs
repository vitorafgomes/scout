using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scout.Audit.Storage.Migrations;

/// <inheritdoc />
internal partial class Initial : Migration
{
    private static readonly string[] StatusStarted = ["status", "started"];

    private static readonly string[] OwnerRepoStarted = ["owner", "repo", "started"];

    private static readonly string[] JobIdSeq = ["job_id", "seq"];

    private static readonly string[] JobIdMergeKey = ["job_id", "merge_key"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "audit_job",
            columns: table => new
            {
                number = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                id = table.Column<string>(type: "TEXT", nullable: false),
                owner = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                repo = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                commit_sha = table.Column<string>(type: "TEXT", nullable: false),
                origin = table.Column<string>(type: "TEXT", nullable: true),
                started = table.Column<DateTime>(type: "TEXT", nullable: false),
                finished = table.Column<DateTime>(type: "TEXT", nullable: true),
                status = table.Column<string>(type: "TEXT", nullable: false),
                paused_reason = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_job", x => x.number);
                table.UniqueConstraint("AK_audit_job_id", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "audit_batch",
            columns: table => new
            {
                job_id = table.Column<string>(type: "TEXT", nullable: false),
                seq = table.Column<int>(type: "INTEGER", nullable: false),
                area = table.Column<string>(type: "TEXT", nullable: false),
                files = table.Column<string>(type: "TEXT", nullable: false),
                status = table.Column<string>(type: "TEXT", nullable: false),
                started = table.Column<DateTime>(type: "TEXT", nullable: true),
                finished = table.Column<DateTime>(type: "TEXT", nullable: true),
                files_read = table.Column<string>(type: "TEXT", nullable: false),
                failure = table.Column<string>(type: "TEXT", nullable: true),
                partial = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_batch", x => new { x.job_id, x.seq });
                table.ForeignKey(
                    name: "FK_audit_batch_audit_job_job_id",
                    column: x => x.job_id,
                    principalTable: "audit_job",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "audit_finding",
            columns: table => new
            {
                job_id = table.Column<string>(type: "TEXT", nullable: false),
                id = table.Column<string>(type: "TEXT", nullable: false),
                merge_key = table.Column<string>(type: "TEXT", nullable: false),
                seq = table.Column<long>(type: "INTEGER", nullable: false),
                severity = table.Column<string>(type: "TEXT", nullable: false),
                title = table.Column<string>(type: "TEXT", nullable: false),
                path = table.Column<string>(type: "TEXT", nullable: false),
                line = table.Column<int>(type: "INTEGER", nullable: false),
                context = table.Column<string>(type: "TEXT", nullable: true),
                why = table.Column<string>(type: "TEXT", nullable: false),
                fix = table.Column<string>(type: "TEXT", nullable: false),
                rule = table.Column<string>(type: "TEXT", nullable: true),
                state = table.Column<string>(type: "TEXT", nullable: false),
                issue_url = table.Column<string>(type: "TEXT", nullable: true),
                dismissed_reason = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_finding", x => new { x.job_id, x.id });
                table.ForeignKey(
                    name: "FK_audit_finding_audit_job_job_id",
                    column: x => x.job_id,
                    principalTable: "audit_job",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_audit_finding_job_id_merge_key",
            table: "audit_finding",
            columns: JobIdMergeKey,
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_audit_finding_job_id_seq",
            table: "audit_finding",
            columns: JobIdSeq);

        migrationBuilder.CreateIndex(
            name: "IX_audit_job_owner_repo_started",
            table: "audit_job",
            columns: OwnerRepoStarted);

        migrationBuilder.CreateIndex(
            name: "IX_audit_job_status_started",
            table: "audit_job",
            columns: StatusStarted);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_batch");

        migrationBuilder.DropTable(
            name: "audit_finding");

        migrationBuilder.DropTable(
            name: "audit_job");
    }
}
