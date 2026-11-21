using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Scout.Review;

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
    /// one with no findings included. A finding <see cref="FindingValidation"/> refuses, or one <paramref name="place"/>
    /// answers a problem for, is left out, and <c>LeftOut</c> counts them in the answer's order; null when none was.
    /// </summary>
    public static (string? Verdict, IReadOnlyList<Finding> Findings, int Dropped, bool Accepted, LeftOutFindings? LeftOut) Read(
        IReadOnlyList<string> blocks, string owner, string repo, string commit, int? pullRequest = null, Func<Finding, (Finding Placed, string? Problem)>? place = null)
    {
        string? verdict = null;
        var findings = new List<Finding>();
        var dropped = 0;
        var accepted = false;
        var leftOut = 0;
        string? first = null;
        var offDiff = new List<(Finding Finding, string Problem)>();
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

            var presented = (payload.Findings ?? []).Where(f => f is not null).ToList();
            if (!string.Equals(payload.Repository?.Trim(), $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(payload.Commit) && (payload.Commit.Trim() is not { Length: >= 7 } named || !commit.StartsWith(named, StringComparison.OrdinalIgnoreCase)))
                || payload.Number != pullRequest)
            {
                dropped += presented.Count;
                continue;
            }

            verdict = payload.Verdict ?? verdict;
            foreach (var finding in presented)
            {
                if (FindingValidation.ProblemOf(finding) is { } problem)
                {
                    leftOut++;
                    first ??= problem;
                }
                else if ((place?.Invoke(finding) ?? (finding, null)) is var (placed, off) && off is not null)
                {
                    leftOut++;
                    first ??= off;
                    offDiff.Add((finding, off));
                }
                else
                {
                    findings.Add(placed);
                }
            }

            accepted = true;
        }

        return (verdict, findings, dropped, accepted, first is null ? null : new LeftOutFindings(leftOut, first, offDiff));
    }

    /// <summary>
    /// The sentence a batch keeps for the <paramref name="count"/> findings it left out: how many, and the first one's
    /// problem as <see cref="FindingValidation"/> words it; null when it left none out.
    /// </summary>
    internal static string? LeftOutSentence(int count, string? first) => count switch
    {
        0 => null,
        1 => $"1 finding left out: it {first}",
        _ => $"{count.ToString(CultureInfo.InvariantCulture)} findings left out: the first {first}",
    };

    /// <summary>The fenced block of <paramref name="findings"/> for the repository at the commit.</summary>
    public static string Write(string owner, string repo, string commit, string verdict, string read, IReadOnlyList<Finding> findings) =>
        Fence + "\n" + JsonSerializer.Serialize(new Payload(AuditKind.Audit, $"{owner}/{repo}", null, commit, verdict, read, findings), Json) + "\n```";

    /// <summary>
    /// The review block <paramref name="block"/> with what Scout's code counted: <c>coverage</c>, the files read of the
    /// files changed; <c>notes</c>, Scout's own sentences, when there are any; and <c>tests</c> as not run with
    /// <paramref name="testsReason"/> when the block carries no test run. Null when <paramref name="block"/> is not one
    /// fenced JSON object.
    /// </summary>
    public static string? Complete(string block, int read, int of, IReadOnlyList<string> notes, string testsReason, Offer? offer = null, IReadOnlySet<string>? unconfirmed = null)
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
        if (unconfirmed is { Count: > 0 } && payload["findings"] is JsonArray presented)
        {
            foreach (var finding in presented.OfType<JsonObject>())
            {
                if (finding["id"]?.GetValue<string>() is { } id && unconfirmed.Contains(id) && finding["title"]?.GetValue<string>() is { } title)
                {
                    finding["title"] = Contestation.UnconfirmedTitle(title);
                }
            }
        }

        if (offer is not null)
        {
            payload["offer"] = new JsonObject { ["verdict"] = offer.Verdict, ["reason"] = offer.Reason, ["commentInstead"] = offer.CommentInstead };
        }

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

    private sealed record Payload(string? Kind, string? Repository, int? Number, string? Commit, string? Verdict, string? Read, IReadOnlyList<Finding>? Findings);
}

/// <summary>
/// The findings a batch's answer left out: how many, the first one's problem in the answer's order, and those of them left
/// out only because Scout could not place them on the pull request's diff, each with its problem.
/// </summary>
internal sealed record LeftOutFindings(int Count, string First, IReadOnlyList<(Finding Finding, string Problem)> OffDiff)
{
    /// <summary>The one sentence the batch keeps for them.</summary>
    public string Sentence => FindingsBlock.LeftOutSentence(Count, First)!;
}
