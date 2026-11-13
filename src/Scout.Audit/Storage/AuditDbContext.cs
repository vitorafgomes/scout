using Microsoft.EntityFrameworkCore;

namespace Scout.Audit.Storage;

/// <summary>The audit database as Entity Framework sees it: <c>audit_job</c>, <c>audit_batch</c> and <c>audit_finding</c>. Used by <see cref="SqliteAuditStore"/> only.</summary>
internal sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditJobRow> Jobs => Set<AuditJobRow>();

    public DbSet<AuditBatchRow> Batches => Set<AuditBatchRow>();

    public DbSet<AuditFindingRow> Findings => Set<AuditFindingRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditJobRow>(job =>
        {
            job.ToTable("audit_job");
            job.HasKey(j => j.Number);
            job.HasAlternateKey(j => j.Id);
            job.HasIndex(j => new { j.Owner, j.Repo, j.Kind, j.PullRequest, j.Started });
            job.HasIndex(j => new { j.Status, j.Started });
            job.Property(j => j.Number).HasColumnName("number");
            job.Property(j => j.Id).HasColumnName("id");
            job.Property(j => j.Owner).HasColumnName("owner").UseCollation("NOCASE");
            job.Property(j => j.Repo).HasColumnName("repo").UseCollation("NOCASE");
            job.Property(j => j.CommitSha).HasColumnName("commit_sha");
            job.Property(j => j.Origin).HasColumnName("origin");
            job.Property(j => j.Started).HasColumnName("started");
            job.Property(j => j.Finished).HasColumnName("finished");
            job.Property(j => j.Status).HasColumnName("status");
            job.Property(j => j.PausedReason).HasColumnName("paused_reason");
            job.Property(j => j.Kind).HasColumnName("kind").HasDefaultValue(AuditKind.Audit);
            job.Property(j => j.PullRequest).HasColumnName("pull_request");
            job.Property(j => j.Preflight).HasColumnName("preflight");
        });

        modelBuilder.Entity<AuditBatchRow>(batch =>
        {
            batch.ToTable("audit_batch");
            batch.HasKey(b => new { b.JobId, b.Seq });
            batch.Property(b => b.JobId).HasColumnName("job_id");
            batch.Property(b => b.Seq).HasColumnName("seq");
            batch.Property(b => b.Area).HasColumnName("area");
            batch.Property(b => b.Files).HasColumnName("files");
            batch.Property(b => b.Status).HasColumnName("status");
            batch.Property(b => b.Started).HasColumnName("started");
            batch.Property(b => b.Finished).HasColumnName("finished");
            batch.Property(b => b.FilesRead).HasColumnName("files_read");
            batch.Property(b => b.Failure).HasColumnName("failure");
            batch.Property(b => b.Partial).HasColumnName("partial");
            batch.HasOne<AuditJobRow>().WithMany().HasForeignKey(b => b.JobId).HasPrincipalKey(j => j.Id).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditFindingRow>(finding =>
        {
            finding.ToTable("audit_finding");
            finding.HasKey(f => new { f.JobId, f.Id });
            finding.HasIndex(f => new { f.JobId, f.MergeKey }).IsUnique();
            finding.HasIndex(f => new { f.JobId, f.Seq });
            finding.Property(f => f.JobId).HasColumnName("job_id");
            finding.Property(f => f.Id).HasColumnName("id");
            finding.Property(f => f.MergeKey).HasColumnName("merge_key");
            finding.Property(f => f.Seq).HasColumnName("seq");
            finding.Property(f => f.Severity).HasColumnName("severity");
            finding.Property(f => f.Title).HasColumnName("title");
            finding.Property(f => f.Path).HasColumnName("path");
            finding.Property(f => f.Line).HasColumnName("line");
            finding.Property(f => f.Context).HasColumnName("context");
            finding.Property(f => f.Why).HasColumnName("why");
            finding.Property(f => f.Fix).HasColumnName("fix");
            finding.Property(f => f.Rule).HasColumnName("rule");
            finding.Property(f => f.State).HasColumnName("state");
            finding.Property(f => f.IssueUrl).HasColumnName("issue_url");
            finding.Property(f => f.DismissedReason).HasColumnName("dismissed_reason");
            finding.HasOne<AuditJobRow>().WithMany().HasForeignKey(f => f.JobId).HasPrincipalKey(j => j.Id).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>
/// A row of <c>audit_job</c>; <see cref="Number"/> keeps the order jobs were started in when two share a start time,
/// <see cref="PullRequest"/> is the pull request a job of kind <c>review</c> is of, and <see cref="Preflight"/> the checks
/// it ran before it started, as JSON, written once with the row.
/// </summary>
internal sealed class AuditJobRow
{
    public long Number { get; set; }

    public required string Id { get; set; }

    public required string Owner { get; set; }

    public required string Repo { get; set; }

    public required string CommitSha { get; set; }

    public string? Origin { get; set; }

    public DateTime Started { get; set; }

    public DateTime? Finished { get; set; }

    public required string Status { get; set; }

    public string? PausedReason { get; set; }

    public required string Kind { get; set; }

    public int? PullRequest { get; set; }

    public string? Preflight { get; set; }
}

/// <summary>A row of <c>audit_batch</c>.</summary>
internal sealed class AuditBatchRow
{
    public required string JobId { get; set; }

    public int Seq { get; set; }

    public required string Area { get; set; }

    public List<string> Files { get; set; } = [];

    public required string Status { get; set; }

    public DateTime? Started { get; set; }

    public DateTime? Finished { get; set; }

    public List<string> FilesRead { get; set; } = [];

    public string? Failure { get; set; }

    public List<string> Partial { get; set; } = [];
}

/// <summary>A row of <c>audit_finding</c>; <see cref="State"/> stays <c>open</c> until the desktop records more.</summary>
internal sealed class AuditFindingRow
{
    public required string JobId { get; set; }

    public required string Id { get; set; }

    public required string MergeKey { get; set; }

    public long Seq { get; set; }

    public required string Severity { get; set; }

    public required string Title { get; set; }

    public required string Path { get; set; }

    public int Line { get; set; }

    public string? Context { get; set; }

    public required string Why { get; set; }

    public required string Fix { get; set; }

    public string? Rule { get; set; }

    public string State { get; set; } = "open";

    public string? IssueUrl { get; set; }

    public string? DismissedReason { get; set; }
}
