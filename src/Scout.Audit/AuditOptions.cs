namespace Scout.Audit;

/// <summary>
/// The audit's settings under <c>Scout:Audit</c>: where its database lives, the caps that split a folder into batches, and
/// how long one batch turn may take. Paths and numbers only, never a credential.
/// </summary>
public sealed class AuditOptions
{
    /// <summary>Configuration section bound to these options.</summary>
    public const string Section = "Scout:Audit";

    /// <summary>The SQLite file of the jobs; empty for <c>Scout/audit.db</c> under the user's local application data.</summary>
    public string DbPath { get; set; } = string.Empty;

    /// <summary>Files one batch holds at most; a folder with more is split.</summary>
    public int MaxBatchFiles { get; set; } = 40;

    /// <summary>
    /// Characters, counted as bytes on disk, one batch holds at most; a folder with more is split. Every file the model reads
    /// is sent again on each round trip of the turn, so this bounds the largest request of a batch.
    /// </summary>
    public long MaxBatchCharacters { get; set; } = 60_000;

    /// <summary>How long one batch turn may take before it is abandoned as not answered in time.</summary>
    public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromMinutes(10);
}
