using Microsoft.EntityFrameworkCore;

namespace Scout.Meetings.Storage;

/// <summary>
/// The meetings database as Entity Framework sees it: <c>meetings</c>, their <c>lines</c> and <c>turns</c>, the
/// <c>keeps</c> keys, and <c>schema_version</c> at 1. A meeting's lines, turns and keep key go with it. Used by
/// <see cref="SqliteMeetingStore"/> only.
/// </summary>
internal sealed class MeetingsDbContext(DbContextOptions<MeetingsDbContext> options) : DbContext(options)
{
    public DbSet<MeetingRow> Meetings => Set<MeetingRow>();

    public DbSet<LineRow> Lines => Set<LineRow>();

    public DbSet<TurnRow> Turns => Set<TurnRow>();

    public DbSet<KeepRow> Keeps => Set<KeepRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SchemaVersionRow>(version =>
        {
            version.ToTable("schema_version");
            version.HasKey(v => v.Version);
            version.Property(v => v.Version).HasColumnName("version").ValueGeneratedNever();
            version.HasData(new SchemaVersionRow { Version = 1 });
        });

        modelBuilder.Entity<MeetingRow>(meeting =>
        {
            meeting.ToTable("meetings");
            meeting.HasKey(m => m.Number);
            meeting.HasAlternateKey(m => m.Id);
            meeting.HasIndex(m => new { m.StartedUtcTicks, m.Number });
            meeting.Property(m => m.Number).HasColumnName("number");
            meeting.Property(m => m.Id).HasColumnName("id");
            meeting.Property(m => m.StartedAt).HasColumnName("started_at");
            meeting.Property(m => m.StartedUtcTicks).HasColumnName("started_utc_ticks");
            meeting.Property(m => m.EndedAt).HasColumnName("ended_at");
            meeting.Property(m => m.Title).HasColumnName("title");
            meeting.HasMany(m => m.Lines).WithOne().HasForeignKey(l => l.MeetingId).HasPrincipalKey(m => m.Id).OnDelete(DeleteBehavior.Cascade);
            meeting.HasMany(m => m.Turns).WithOne().HasForeignKey(t => t.MeetingId).HasPrincipalKey(m => m.Id).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LineRow>(line =>
        {
            line.ToTable("lines");
            line.HasKey(l => new { l.MeetingId, l.Seq });
            line.Property(l => l.MeetingId).HasColumnName("meeting_id");
            line.Property(l => l.Seq).HasColumnName("seq");
            line.Property(l => l.At).HasColumnName("at");
            line.Property(l => l.Lane).HasColumnName("lane");
            line.Property(l => l.Text).HasColumnName("text");
            line.Property(l => l.Translation).HasColumnName("translation");
            line.Property(l => l.Language).HasColumnName("language");
        });

        modelBuilder.Entity<TurnRow>(turn =>
        {
            turn.ToTable("turns");
            turn.HasKey(t => new { t.MeetingId, t.Seq });
            turn.Property(t => t.MeetingId).HasColumnName("meeting_id");
            turn.Property(t => t.Seq).HasColumnName("seq");
            turn.Property(t => t.At).HasColumnName("at");
            turn.Property(t => t.Question).HasColumnName("question");
            turn.Property(t => t.Answer).HasColumnName("answer");
            turn.Property(t => t.Screenshot).HasColumnName("screenshot");
            turn.Property(t => t.Transcript).HasColumnName("transcript");
            turn.Property(t => t.ScreenText).HasColumnName("screen_text");
        });

        modelBuilder.Entity<KeepRow>(keep =>
        {
            keep.ToTable("keeps");
            keep.HasKey(k => k.Key);
            keep.Property(k => k.Key).HasColumnName("key");
            keep.Property(k => k.MeetingId).HasColumnName("meeting_id");
            keep.HasOne<MeetingRow>().WithMany().HasForeignKey(k => k.MeetingId).HasPrincipalKey(m => m.Id).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>The one row of <c>schema_version</c>.</summary>
internal sealed class SchemaVersionRow
{
    public int Version { get; set; }
}

/// <summary>
/// A row of <c>meetings</c>. <see cref="StartedAt"/> keeps the offset the desktop sent; <see cref="StartedUtcTicks"/> is
/// the same instant in UTC ticks, which the list sorts by, and <see cref="Number"/> keeps the order meetings were kept in.
/// </summary>
internal sealed class MeetingRow
{
    public long Number { get; set; }

    public required string Id { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public long StartedUtcTicks { get; set; }

    public DateTimeOffset EndedAt { get; set; }

    public required string Title { get; set; }

    public List<LineRow> Lines { get; set; } = [];

    public List<TurnRow> Turns { get; set; } = [];
}

/// <summary>A row of <c>lines</c>, numbered from 1 in the order given.</summary>
internal sealed class LineRow
{
    public string MeetingId { get; set; } = "";

    public int Seq { get; set; }

    public DateTimeOffset At { get; set; }

    public required string Lane { get; set; }

    public required string Text { get; set; }

    public string? Translation { get; set; }

    public string? Language { get; set; }
}

/// <summary>A row of <c>turns</c>, numbered from 1 in the order given.</summary>
internal sealed class TurnRow
{
    public string MeetingId { get; set; } = "";

    public int Seq { get; set; }

    public DateTimeOffset At { get; set; }

    public required string Question { get; set; }

    public required string Answer { get; set; }

    public bool Screenshot { get; set; }

    public bool Transcript { get; set; }

    public bool ScreenText { get; set; }
}

/// <summary>A row of <c>keeps</c>: the keep key a meeting was kept under.</summary>
internal sealed class KeepRow
{
    public required string Key { get; set; }

    public required string MeetingId { get; set; }
}
