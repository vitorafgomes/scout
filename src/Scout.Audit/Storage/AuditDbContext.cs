using Microsoft.EntityFrameworkCore;

namespace Scout.Audit.Storage;

/// <summary>The audit database as Entity Framework sees it: <c>audit_job</c>, <c>audit_batch</c>, <c>audit_finding</c> and <c>audit_turn</c>. Used by <see cref="SqliteAuditStore"/> only.</summary>
internal sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditJobRow> Jobs => Set<AuditJobRow>();

    public DbSet<AuditBatchRow> Batches => Set<AuditBatchRow>();

    public DbSet<AuditFindingRow> Findings => Set<AuditFindingRow>();

    public DbSet<AuditTurnRow> Turns => Set<AuditTurnRow>();

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
            job.Property(j => j.Resumed).HasColumnName("resumed");
            job.Property(j => j.Inputs).HasColumnName("inputs");
            job.Property(j => j.ScoutVersion).HasColumnName("scout_version");
            job.Property(j => j.ChatModel).HasColumnName("chat_model");
        });

        modelBuilder.Entity<AuditTurnRow>(turn =>
        {
            turn.ToTable("audit_turn");
            turn.HasKey(t => t.Number);
            turn.HasIndex(t => t.JobId);
            turn.Property(t => t.Number).HasColumnName("number");
            turn.Property(t => t.JobId).HasColumnName("job_id");
            turn.Property(t => t.Batch).HasColumnName("batch");
            turn.Property(t => t.Reading).HasColumnName("reading");
            turn.Property(t => t.ContestGroup).HasColumnName("contest_group");
            turn.Property(t => t.Attempt).HasColumnName("attempt");
            turn.Property(t => t.Extra).HasColumnName("extra_request");
            turn.Property(t => t.Started).HasColumnName("started");
            turn.Property(t => t.ElapsedMs).HasColumnName("elapsed_ms");
            turn.Property(t => t.RequestLength).HasColumnName("request_length");
            turn.Property(t => t.Calls).HasColumnName("calls");
            turn.Property(t => t.Presented).HasColumnName("presented");
            turn.Property(t => t.Placed).HasColumnName("placed");
            turn.Property(t => t.LeftOut).HasColumnName("left_out");
            turn.Property(t => t.Failure).HasColumnName("failure");
            turn.Property(t => t.Held).HasColumnName("held");
            turn.Property(t => t.Refuted).HasColumnName("refuted");
            turn.Property(t => t.Uncertain).HasColumnName("uncertain");
            turn.HasOne<AuditJobRow>().WithMany().HasForeignKey(t => t.JobId).HasPrincipalKey(j => j.Id).OnDelete(DeleteBehavior.Cascade);
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
            batch.Property(b => b.LeftOut).HasColumnName("left_out");
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
            finding.Property(f => f.Evidence).HasColumnName("evidence");
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

    /// <summary>When the job last resumed from a pause, in UTC; null for one never paused and resumed.</summary>
    public DateTime? Resumed { get; set; }

    /// <summary>The inputs a review's batches are handed, as JSON, written once with the row; null for an audit and a job kept before they existed.</summary>
    public string? Inputs { get; set; }

    /// <summary>The Scout version that last ran the job, written when a worker starts or resumes it; null before it existed.</summary>
    public string? ScoutVersion { get; set; }

    /// <summary>The provider and chat model names, <c>provider/model</c>, that last ran the job; names only, never a key.</summary>
    public string? ChatModel { get; set; }
}

/// <summary>
/// A row of <c>audit_turn</c>: the numbers of one model turn of a job, and names; never the model's text, a file's content
/// or a function's arguments.
/// </summary>
internal sealed class AuditTurnRow
{
    public long Number { get; set; }

    public required string JobId { get; set; }

    /// <summary>The batch's order; null for the second reading.</summary>
    public int? Batch { get; set; }

    /// <summary>general, removed, requests or contest.</summary>
    public required string Reading { get; set; }

    /// <summary>The second reading's group, from 1; null for a batch turn.</summary>
    public int? ContestGroup { get; set; }

    public int Attempt { get; set; }

    /// <summary>True for the extra request that asks a batch again for the findings it could not place.</summary>
    public bool Extra { get; set; }

    public DateTime Started { get; set; }

    public long ElapsedMs { get; set; }

    public int RequestLength { get; set; }

    /// <summary>How many times each function was called, as a JSON object of names to counts.</summary>
    public required string Calls { get; set; }

    public int Presented { get; set; }

    public int Placed { get; set; }

    public int LeftOut { get; set; }

    public string? Failure { get; set; }

    public int? Held { get; set; }

    public int? Refuted { get; set; }

    public int? Uncertain { get; set; }
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

    public string? LeftOut { get; set; }
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

    public string? Evidence { get; set; }

    public required string Why { get; set; }

    public required string Fix { get; set; }

    public string? Rule { get; set; }

    public string State { get; set; } = "open";

    public string? IssueUrl { get; set; }

    public string? DismissedReason { get; set; }
}
