using System.Globalization;

namespace Scout.Review.Tests;

/// <summary>Which conversations of a pull request are Scout's own earlier findings, each with its replies.</summary>
public sealed class EarlierFindingsTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-20T10:00:00Z", CultureInfo.InvariantCulture);

    private static readonly string Scouts = FindingComment.Write(new Finding("a", Severities.Fix, "Null lookup", "src/A.cs", 9, null, "The lookup is not checked.", "Check it.", null));

    private static ReviewCommentRead C(long id, string author, string body, long? replyTo = null, int? line = 9, int minutes = 0, string path = "src/A.cs") =>
        new(id, replyTo, author, body, path, line, 4, "aaa", At.AddMinutes(minutes), string.Create(CultureInfo.InvariantCulture, $"https://example.invalid/r{id}"));

    [Fact]
    public void E1_a_conversation_is_scout_s_when_its_first_comment_is_in_scout_s_form_and_by_a_login_scout_signs_with()
    {
        ReviewCommentRead[] comments =
        [
            C(1, "scout-bot[bot]", Scouts),
            C(2, "owner", Scouts, minutes: 1),
            C(3, "someone", Scouts, minutes: 2),
            C(4, "scout-bot", "A note by hand.", minutes: 3),
            C(5, "alice", "It is intended.", replyTo: 1, minutes: 4),
            C(6, "scout-bot", "Thanks.", replyTo: 1, minutes: 5),
        ];

        var earlier = EarlierFindings.Own(comments, ["scout-bot", "owner"]);

        earlier.Findings.Select(f => (f.Id, f.Url)).ShouldBe(
            [("e1", "https://example.invalid/r1"), ("e2", "https://example.invalid/r2")],
            "the bot's and the owner's conversations in Scout's form are Scout's, whatever form GitHub gives the bot's login; another author's, and a comment not in the form, are not");
        earlier.Findings[0].Replies.Select(r => (r.Author, r.Body)).ShouldBe([("alice", "It is intended."), ("scout-bot", "Thanks.")], "the replies come with their conversation, oldest first");
        earlier.Findings[0].Finding.ShouldBe(FindingComment.Read(Scouts), "the finding is the one its comment carries");
        earlier.More.ShouldBeFalse();
    }

    [Fact]
    public void E1_an_outdated_conversation_is_kept_with_its_original_line_and_replies_are_bounded()
    {
        ReviewCommentRead[] comments =
        [
            C(1, "owner", Scouts, line: null),
            .. Enumerable.Range(0, EarlierFindings.MaxReplies + 3).Select(i => C(100 + i, "alice", new string('r', EarlierFindings.MaxReplyLength + 50), replyTo: 1, minutes: i + 1)),
        ];

        var finding = EarlierFindings.Own(comments, ["owner"]).Findings.ShouldHaveSingleItem();

        (finding.Outdated, finding.OriginalLine, finding.Commit).ShouldBe((true, (int?)4, "aaa"), "a conversation whose line a later commit changed is outdated and keeps where it was written");
        finding.Replies.Count.ShouldBe(EarlierFindings.MaxReplies, "the replies of one conversation are bounded");
        finding.Replies.ShouldAllBe(r => r.Body.Length == EarlierFindings.MaxReplyLength, "each reply is cut to its bound");
    }

    [Fact]
    public void E1_more_conversations_than_the_bound_say_so_and_the_first_ones_are_kept()
    {
        var comments = Enumerable.Range(1, EarlierFindings.MaxFindings + 2).Select(i => C(i, "owner", Scouts, minutes: i)).ToList();

        var earlier = EarlierFindings.Own(comments, ["owner"]);

        (earlier.Findings.Count, earlier.More, earlier.Findings[0].Url).ShouldBe(
            (EarlierFindings.MaxFindings, true, "https://example.invalid/r1"), "the first conversations opened are kept, and the rest are said to be there");
        EarlierFindings.Own([], ["owner"], moreComments: true).More.ShouldBeTrue("a read that did not take every comment says there may be more");
    }

    [Theory]
    [InlineData("scout-bot[bot]", "scout-bot", true)]
    [InlineData("Scout-Bot", "scout-bot[BOT]", true)]
    [InlineData("owner", "owner2", false)]
    [InlineData("", "", false)]
    public void Logins_are_the_same_account_in_any_case_with_or_without_the_bot_suffix(string one, string other, bool same) =>
        EarlierFindings.SameLogin(one, other).ShouldBe(same, $"{one} and {other}");
}
