using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// The verdict and the findings of the audit blocks among <paramref name="blocks"/> for the repository at the commit, or,
    /// when <paramref name="pullRequest"/> is given, of the review blocks, which carry no kind; a block of the other kind or
    /// an unreadable one is skipped, and the findings of a block for another repository, commit or pull request are counted
    /// as dropped. A block that names no commit is taken as the job's. <c>Accepted</c> says whether any block was taken,
    /// one with no findings included.
    /// </summary>
    public static (string? Verdict, IReadOnlyList<AuditFinding> Findings, int Dropped, bool Accepted) Read(
        IReadOnlyList<string> blocks, string owner, string repo, string commit, int? pullRequest = null)
    {
        string? verdict = null;
        var findings = new List<AuditFinding>();
        var dropped = 0;
        var accepted = false;
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

            if (payload is null || payload.Kind != (pullRequest is null ? AuditKind.Audit : null))
            {
                continue;
            }

            var presented = (payload.Findings ?? []).Where(f => f.Title is not null && f.Path is not null).ToList();
            if (!string.Equals(payload.Repository?.Trim(), $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(payload.Commit) && (payload.Commit.Trim() is not { Length: >= 7 } named || !commit.StartsWith(named, StringComparison.OrdinalIgnoreCase)))
                || payload.Number != pullRequest)
            {
                dropped += presented.Count;
                continue;
            }

            verdict = payload.Verdict ?? verdict;
            findings.AddRange(presented);
            accepted = true;
        }

        return (verdict, findings, dropped, accepted);
    }

    /// <summary>The fenced block of <paramref name="findings"/> for the repository at the commit.</summary>
    public static string Write(string owner, string repo, string commit, string verdict, string read, IReadOnlyList<AuditFinding> findings) =>
        Fence + "\n" + JsonSerializer.Serialize(new Payload(AuditKind.Audit, $"{owner}/{repo}", null, commit, verdict, read, findings), Json) + "\n```";

    /// <summary>
    /// The review block <paramref name="block"/> with what Scout's code counted: <c>coverage</c>, the files read of the
    /// files changed; <c>notes</c>, Scout's own sentences, when there are any; and <c>tests</c> as not run with
    /// <paramref name="testsReason"/> when the block carries no test run. Null when <paramref name="block"/> is not one
    /// fenced JSON object.
    /// </summary>
    public static string? Complete(string block, int read, int of, IReadOnlyList<string> notes, string testsReason)
    {
        var start = block.IndexOf('\n', StringComparison.Ordinal);
        var end = block.LastIndexOf("```", StringComparison.Ordinal);
        if (!block.StartsWith(Fence, StringComparison.Ordinal) || start < 0 || end <= start)
        {
            return null;
        }

        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(block[(start + 1)..end]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null)
        {
            return null;
        }

        payload["coverage"] = new JsonObject { ["read"] = read, ["of"] = of };
        if (notes.Count > 0)
        {
            payload["notes"] = new JsonArray([.. notes.Select(n => (JsonNode?)JsonValue.Create(n))]);
        }

        if (payload["tests"] is null)
        {
            payload["tests"] = new JsonObject { ["ran"] = false, ["reason"] = testsReason };
        }

        return Fence + "\n" + payload.ToJsonString() + "\n```";
    }

    private sealed record Payload(string? Kind, string? Repository, int? Number, string? Commit, string? Verdict, string? Read, IReadOnlyList<AuditFinding>? Findings);
}
