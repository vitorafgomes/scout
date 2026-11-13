using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scout.Audit;

/// <summary>
/// Reads the audit findings out of the <c>scout-findings</c> blocks a batch turn attached, and writes the block a batch
/// posts with only the findings new to the job, in the shape the desktop renders as cards: kind <c>audit</c>, the
/// repository, the commit, the verdict, what was read, and the findings.
/// </summary>
internal static class FindingsBlock
{
    internal const string Fence = "```scout-findings";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The verdict and the findings of the audit blocks among <paramref name="blocks"/> for the repository at the commit; a
    /// review block or an unreadable one is skipped, and the findings of a block for another repository or commit are
    /// counted as dropped. A block that names no commit is taken as the job's.
    /// </summary>
    public static (string? Verdict, IReadOnlyList<AuditFinding> Findings, int Dropped) Read(IReadOnlyList<string> blocks, string owner, string repo, string commit)
    {
        string? verdict = null;
        var findings = new List<AuditFinding>();
        var dropped = 0;
        foreach (var block in blocks)
        {
            var start = block.IndexOf('\n', StringComparison.Ordinal);
            var end = block.LastIndexOf("```", StringComparison.Ordinal);
            if (!block.StartsWith(Fence, StringComparison.Ordinal) || start < 0 || end <= start)
            {
                continue;
            }

            Payload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<Payload>(block[(start + 1)..end], Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (payload is not { Kind: "audit" })
            {
                continue;
            }

            var presented = (payload.Findings ?? []).Where(f => f.Title is not null && f.Path is not null).ToList();
            if (!string.Equals(payload.Repository?.Trim(), $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(payload.Commit) && (payload.Commit.Trim() is not { Length: >= 7 } named || !commit.StartsWith(named, StringComparison.OrdinalIgnoreCase))))
            {
                dropped += presented.Count;
                continue;
            }

            verdict = payload.Verdict ?? verdict;
            findings.AddRange(presented);
        }

        return (verdict, findings, dropped);
    }

    /// <summary>The fenced block of <paramref name="findings"/> for the repository at the commit.</summary>
    public static string Write(string owner, string repo, string commit, string verdict, string read, IReadOnlyList<AuditFinding> findings) =>
        Fence + "\n" + JsonSerializer.Serialize(new Payload("audit", $"{owner}/{repo}", commit, verdict, read, findings), Json) + "\n```";

    private sealed record Payload(string? Kind, string? Repository, string? Commit, string? Verdict, string? Read, IReadOnlyList<AuditFinding>? Findings);
}
