using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scout.Plugin.GitHub.Abstractions;
using Scout.Review;

namespace Scout.Plugin.GitHub;

/// <summary>
/// What a review note is composed from: the pull request, the verdict (<c>approve</c>, <c>request_changes</c> or
/// <c>comment</c>), the commit reviewed, the findings, and what code measured: the checks read on the commit, the kept
/// test run, the files read of the files changed with the notes on those not read, and who signs.
/// </summary>
internal sealed record ReviewNoteInput(
    string Owner,
    string Repo,
    int Number,
    string Verdict,
    string Commit,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<PullRequestCheck>? Checks,
    ReviewTests? Tests,
    int? FilesRead,
    int? FilesChanged,
    IReadOnlyList<string>? Notes,
    string? SignerLogin,
    bool SignedByBot,
    bool EveryFindingIsAComment);

/// <summary>
/// The review note Scout posts on a pull request and the comment it posts on a finding's line, composed by code in the
/// layout of the GitHub notes artboard: each result written as its shortcode and its word, and a section left out when
/// nothing measured it. No I/O.
/// </summary>
internal static class ReviewNote
{
    /// <summary>The most characters a review note may have; a longer one first loses the checks' table, then the notes.</summary>
    internal const int MaxBodyLength = 60_000;

    /// <summary>The most characters a comment on a line may have.</summary>
    internal const int MaxCommentLength = 10_000;

    internal const string Approve = "approve";
    internal const string RequestChanges = "request_changes";
    internal const string Commenting = "comment";

    /// <summary>
    /// The review note, or null when it cannot fit <see cref="MaxBodyLength"/> even without the checks' table and the notes.
    /// </summary>
    internal static string? Body(ReviewNoteInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        foreach (var (checksTable, notes) in new[] { (true, true), (false, true), (false, false) })
        {
            var body = Compose(input, checksTable, notes);
            if (body.Length <= MaxBodyLength)
            {
                return body;
            }
        }

        return null;
    }

    /// <summary>The comment on a finding's line: its severity and title, what is wrong, why it matters, the fix and its rule.</summary>
    internal static string Comment(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        List<string> lines = [$"{SeverityIcon(finding.Severity)} **{SeverityWord(finding.Severity)}** · {finding.Title}", string.Empty, $"**What is wrong:** {finding.Why}"];
        if (!string.IsNullOrWhiteSpace(finding.Context))
        {
            lines.AddRange([string.Empty, $"**Why it matters:** {finding.Context}"]);
        }

        lines.AddRange([string.Empty, $"**Proposed fix:** {finding.Fix}"]);
        if (!string.IsNullOrWhiteSpace(finding.Rule))
        {
            lines.AddRange([string.Empty, $"**Rule or document:** {finding.Rule}"]);
        }

        return string.Join('\n', lines);
    }

    /// <summary>The first 16 hexadecimal characters of the SHA-256 of the verdict and the findings as JSON.</summary>
    internal static string Digest(string verdict, IReadOnlyList<Finding> findings, int? filesRead, int? filesChanged, IReadOnlyList<string>? notes)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            verdict,
            findings = findings.Select(f => new { f.Id, f.Severity, f.Title, f.Path, f.Line, f.Context, f.Why, f.Fix, f.Rule }),
            filesRead,
            filesChanged,
            notes,
        });
        return Convert.ToHexStringLower(SHA256.HashData(json))[..16];
    }

    /// <summary>The hidden line that marks a verdict's note with its digest.</summary>
    internal static string Marker(string digest) => $"<!-- scout-review {digest} -->";

    private static string Compose(ReviewNoteInput input, bool checksTable, bool notes)
    {
        var commit = Short(input.Commit);
        List<string> lines =
        [
            $"### :robot: Scout review: {Heading(input.Verdict)}",
            string.Empty,
            Bar(input),
            string.Empty,
            $"Commit `{commit}`",
            string.Empty,
        ];
        if (input is { FilesRead: { } read, FilesChanged: { } of })
        {
            lines.AddRange(Changed(read, of, notes ? input.Notes : null));
        }

        if (input.Tests is { } tests)
        {
            lines.AddRange(Ran(tests));
        }

        if (input.Checks is { } checks)
        {
            lines.AddRange(Checks(checks, commit, checksTable));
        }

        lines.AddRange(Findings(input));
        lines.AddRange(Section(
            "microscope", "How this review was made",
            "| Item | Value |",
            "|---|---|",
            $"| Commit | `{commit}` |",
            string.Empty,
            "- **Scout cannot see:** how the application behaves when it runs, the secrets of the repository, a private dependency it could not restore.",
            "- **Measured by code:** counts, paths, lines, durations, commands and check results.",
            "- **Written by the AI source from what it read:** the findings."));
        if (Next(input) is { } next)
        {
            lines.AddRange(Section("arrow_right", "What happens next", next));
        }

        lines.Add("---");
        lines.Add(string.Empty);
        lines.Add(string.IsNullOrWhiteSpace(input.SignerLogin)
            ? "Posted after the owner's approval in Scout Desktop"
            : $"Posted after the owner's approval in Scout Desktop · signed as @{input.SignerLogin}");
        if (input.Verdict is Approve or RequestChanges)
        {
            lines.Add(string.Empty);
            lines.Add(Marker(Digest(input.Verdict, input.Findings, input.FilesRead, input.FilesChanged, input.Notes)));
        }

        return string.Join('\n', lines);
    }

    private static string Heading(string verdict) => verdict switch
    {
        Approve => "approved",
        RequestChanges => "changes requested",
        _ => "commented, not approved",
    };

    private static string Bar(ReviewNoteInput input)
    {
        List<string> parts = [input.Verdict switch
        {
            Approve => "> :white_check_mark: **Approved**",
            RequestChanges => "> :x: **Changes requested**",
            _ => "> :speech_balloon: **Commented, not approved**",
        }];
        if (input.Tests is { Ran: true } tests)
        {
            parts.Add(tests.Unrunnable is not null ? "tests: could not run" : tests.ExitCode == 0 ? "tests: passed" : "tests: failed");
        }

        if (input.Checks is { Count: > 0 } checks)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"checks: {checks.Count(Passed)} of {checks.Count} passed"));
        }

        parts.Add(Counts(input.Findings, "findings: "));
        return string.Join(" · ", parts);
    }

    private static string Counts(IReadOnlyList<Finding> findings, string prefix) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}{findings.Count(f => f.Severity == "block")} blocking, {findings.Count(f => f.Severity == "fix")} to fix, {findings.Count(f => f.Severity == "nit")} optional");

    private static List<string> Changed(int read, int of, IReadOnlyList<string>? notes)
    {
        List<string> lines = [string.Create(CultureInfo.InvariantCulture, $"{read} of {of} changed files read.")];
        if (of > read)
        {
            lines.AddRange([string.Empty, string.Create(CultureInfo.InvariantCulture, $"**Not read:** {of - read} files.")]);
            if (notes is { Count: > 0 })
            {
                lines.Add(string.Empty);
                lines.AddRange(notes.Select(n => $"- {n}"));
            }
        }

        return Section("mag", "What changed, by area", [.. lines]);
    }

    private static List<string> Ran(ReviewTests tests)
    {
        if (!tests.Ran)
        {
            var reason = string.IsNullOrWhiteSpace(tests.Reason) ? "Not run." : $"Not run: {tests.Reason.Trim()}";
            return Section("arrow_forward", "Tests Scout ran", reason.EndsWith('.') ? reason : reason + ".");
        }

        List<string> lines = ["| Item | Measured |", "|---|---|"];
        if (!string.IsNullOrWhiteSpace(tests.Command))
        {
            lines.Add($"| Command | `{Code(tests.Command)}` |");
        }

        if (!string.IsNullOrWhiteSpace(tests.Image))
        {
            lines.Add($"| Where | Isolated container, no network, image `{Code(tests.Image)}` |");
        }

        lines.Add($"| Result | {(tests.Unrunnable is { } unrunnable ? Cell(unrunnable) : tests.ExitCode == 0 ? ":white_check_mark: Passed" : ":x: Failed")} |");
        if (tests.DurationSeconds is { } seconds)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"| Duration | {Math.Round(seconds, MidpointRounding.AwayFromZero):0} s |"));
        }

        if (!string.IsNullOrWhiteSpace(tests.Note))
        {
            lines.AddRange([string.Empty, "**Left out:**", string.Empty, $"- {tests.Note.Trim()}"]);
        }

        return Section("arrow_forward", "Tests Scout ran", [.. lines]);
    }

    private static List<string> Checks(IReadOnlyList<PullRequestCheck> checks, string commit, bool table)
    {
        if (checks.Count == 0)
        {
            return Section("vertical_traffic_light", "Checks on the commit", "No checks on this commit.");
        }

        var passed = checks.Count(Passed);
        var skipped = checks.Count(c => Completed(c) && c.Conclusion == "skipped");
        var failed = checks.Count(c => Completed(c) && !Passed(c) && c.Conclusion != "skipped");
        List<string> lines =
        [
            string.Create(CultureInfo.InvariantCulture, $":white_check_mark: {passed} passed, :x: {failed} failed, :fast_forward: {skipped} skipped, of {checks.Count} on `{commit}`."),
        ];
        if (table)
        {
            lines.AddRange(
            [
                string.Empty,
                string.Create(CultureInfo.InvariantCulture, $"<details><summary>The {checks.Count} checks</summary>"),
                string.Empty,
                "| Check | Result | Link |",
                "|---|---|---|",
                .. checks.Select(c => $"| {Cell(c.Name)} | {CheckResult(c)} | {JobLink(c.Url)} |"),
                string.Empty,
                "</details>",
            ]);
        }

        return Section("vertical_traffic_light", "Checks on the commit", [.. lines]);
    }

    /// <summary>
    /// The link to a check's job, only for an absolute http or https address; a parenthesis or a pipe, which would end
    /// the link or the cell, is percent-encoded. Any other address leaves the cell empty.
    /// </summary>
    private static string JobLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? $"[job]({uri.AbsoluteUri.Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal).Replace("|", "%7C", StringComparison.Ordinal)})"
            : string.Empty;

    private static string CheckResult(PullRequestCheck check) =>
        !Completed(check) ? ":hourglass_flowing_sand: Running"
        : Passed(check) ? ":white_check_mark: Passed"
        : check.Conclusion == "skipped" ? ":fast_forward: Skipped"
        : ":x: Failed";

    private static bool Completed(PullRequestCheck check) => check.Status == "completed";

    private static bool Passed(PullRequestCheck check) => Completed(check) && check.Conclusion is "success" or "neutral";

    private static List<string> Findings(ReviewNoteInput input)
    {
        if (input.Findings.Count == 0)
        {
            return Section("triangular_flag_on_post", "Findings", "None.");
        }

        var sentence = Counts(input.Findings, string.Empty) + "." + (input.EveryFindingIsAComment ? " Each one is also a comment on its line." : string.Empty);
        return Section(
            "triangular_flag_on_post", "Findings",
            [
                sentence,
                string.Empty,
                "| Severity | Where | What is wrong | Rule it breaks |",
                "|---|---|---|---|",
                .. input.Findings.Select(f => string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {SeverityIcon(f.Severity)} {(f.Severity == "nit" ? "Suggestion, optional" : SeverityWord(f.Severity))} | [`{Code(f.Path)}:{f.Line}`]({LineUrl(input, f)}) | {Cell(f.Title)} | {(string.IsNullOrWhiteSpace(f.Rule) ? f.Severity == "nit" ? "None, optional" : "None" : Cell(f.Rule))} |")),
            ]);
    }

    private static string LineUrl(ReviewNoteInput input, Finding finding) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"https://github.com/{input.Owner}/{input.Repo}/pull/{input.Number}/files#diff-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(finding.Path)))}R{finding.Line}");

    private static string? Next(ReviewNoteInput input)
    {
        var mention = input.SignedByBot && !string.IsNullOrWhiteSpace(input.SignerLogin) ? $"@{input.SignerLogin}" : null;
        return input.Verdict switch
        {
            Approve => mention is null
                ? "Nothing is asked of the author. A new commit makes this approval stale."
                : $"Nothing is asked of the author. A new commit makes this approval stale: mention {mention} again.",
            RequestChanges => mention is null ? null : $"Push a fix and mention {mention} again. Scout reviews the new commit from the start.",
            _ => mention is null
                ? "Apply the suggestions you agree with, or leave them. Scout did not approve: a person approves."
                : $"Apply the suggestions you agree with, or leave them. Scout did not approve: a person approves, or mention {mention} after a new commit.",
        };
    }

    private static string SeverityIcon(string severity) => severity switch
    {
        "block" => ":no_entry:",
        "fix" => ":wrench:",
        _ => ":bulb:",
    };

    private static string SeverityWord(string severity) => severity switch
    {
        "block" => "Blocking",
        "fix" => "Fix",
        _ => "Suggestion",
    };

    private static List<string> Section(string shortcode, string title, params string[] lines) =>
        [$"#### :{shortcode}: {title}", string.Empty, .. lines, string.Empty];

    private static string Cell(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>A cell's text inside a code span: a backtick, which would end the span, becomes an apostrophe.</summary>
    private static string Code(string text) => Cell(text).Replace('`', '\'');

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;
}
