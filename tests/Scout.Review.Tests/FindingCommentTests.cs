namespace Scout.Review.Tests;

/// <summary>The one form of Scout's comment on a finding's line, written and read back.</summary>
public sealed class FindingCommentTests
{
    private static Finding F(string severity, string? context = "A payment logs the whole request.", string? rule = "Never log a payment card.", string why = "The card number is written to the log.") =>
        new("a", severity, "The log keeps the card number", "src/Orders/OrderLog.cs", 34, context, why, "Log the order id only.", rule);

    [Theory]
    [InlineData(Severities.Block, "A payment logs the whole request.", "Never log a payment card.")]
    [InlineData(Severities.Fix, null, "Never log a payment card.")]
    [InlineData(Severities.Nit, "A payment logs the whole request.", null)]
    [InlineData(Severities.Fix, null, null)]
    public void E1_a_comment_scout_wrote_is_read_back_as_the_finding_it_carries(string severity, string? context, string? rule)
    {
        var finding = F(severity, context, rule);

        var read = FindingComment.Read(FindingComment.Write(finding));

        read.ShouldBe(
            new CommentedFinding(severity, finding.Title, finding.Why, context, finding.Fix, rule),
            "Scout's own comment gives back its finding's severity, title, why, context, fix and rule");
    }

    [Fact]
    public void E1_a_why_over_several_lines_is_read_back_whole()
    {
        var finding = F(Severities.Block, why: "The card number is written to the log.\nEvery retry writes it again.");

        FindingComment.Read(FindingComment.Write(finding)).ShouldNotBeNull().Why.ShouldBe(finding.Why, "a why with a line break is kept whole");
    }

    [Theory]
    [InlineData("Looks good to me.", "a reply in the author's words")]
    [InlineData(":wrench: **Blocking** · The log keeps the card number\n\n**What is wrong:** W\n\n**Proposed fix:** F", "an icon and a word that name different severities")]
    [InlineData(":wrench: **Fix** · The log keeps the card number\n\n**What is wrong:** W", "no proposed fix")]
    [InlineData("> :wrench: **Fix** · T\n\n**What is wrong:** W\n\n**Proposed fix:** F", "a quote of Scout's comment in someone's reply")]
    [InlineData("", "an empty comment")]
    public void E1_a_comment_that_is_not_in_scout_s_form_is_not_a_finding(string body, string what)
    {
        FindingComment.Read(body).ShouldBeNull($"{what} is not one of Scout's findings");
    }

    [Fact]
    public void The_form_is_the_one_the_review_note_posts_on_a_finding_s_line()
    {
        FindingComment.Write(F(Severities.Block)).ShouldBe(
            ":no_entry: **Blocking** · The log keeps the card number\n\n**What is wrong:** The card number is written to the log.\n\n**Why it matters:** A payment logs the whole request.\n\n**Proposed fix:** Log the order id only.\n\n**Rule or document:** Never log a payment card.",
            "the written form is unchanged by its move into Scout.Review");
    }
}
