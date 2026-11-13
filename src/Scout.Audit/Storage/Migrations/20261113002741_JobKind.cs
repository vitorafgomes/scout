using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scout.Audit.Storage.Migrations;

/// <inheritdoc />
internal partial class JobKind : Migration
{
    private static readonly string[] OwnerRepoKindPullRequestStarted = ["owner", "repo", "kind", "pull_request", "started"];

    private static readonly string[] OwnerRepoStarted = ["owner", "repo", "started"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_audit_job_owner_repo_started",
            table: "audit_job");

        migrationBuilder.AddColumn<string>(
            name: "kind",
            table: "audit_job",
            type: "TEXT",
            nullable: false,
            defaultValue: "audit");

        migrationBuilder.AddColumn<int>(
            name: "pull_request",
            table: "audit_job",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_audit_job_owner_repo_kind_pull_request_started",
            table: "audit_job",
            columns: OwnerRepoKindPullRequestStarted);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_audit_job_owner_repo_kind_pull_request_started",
            table: "audit_job");

        migrationBuilder.DropColumn(
            name: "kind",
            table: "audit_job");

        migrationBuilder.DropColumn(
            name: "pull_request",
            table: "audit_job");

        migrationBuilder.CreateIndex(
            name: "IX_audit_job_owner_repo_started",
            table: "audit_job",
            columns: OwnerRepoStarted);
    }
}
