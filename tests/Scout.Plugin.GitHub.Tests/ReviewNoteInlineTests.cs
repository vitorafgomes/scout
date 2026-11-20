namespace Scout.Plugin.GitHub.Tests;

public sealed class ReviewNoteInlineTests
{
    private static readonly ReviewFinding Finding = new(
        "a", "block", "The log keeps the card number", "src/Orders/OrderLog.cs", 34, "A payment logs the whole request.",
        "The card number is written to the log.", "Log the order id only.", "Never log a payment card.");

    [Fact]
    public void A_Comment_Is_Every_Part_In_The_Artboards_Order() =>
        ReviewNote.Comment(Finding).ShouldBe(
            """
            :no_entry: **Blocking** · The log keeps the card number

            **What is wrong:** The card number is written to the log.

            **Why it matters:** A payment logs the whole request.

            **Proposed fix:** Log the order id only.

            **Rule or document:** Never log a payment card.
            """,
            "a comment on a line follows the artboard's inline note, a blank line between its parts");

    [Fact]
    public void The_First_Line_Is_The_Severity_In_Bold_Beside_Its_Shortcode_And_The_Title() =>
        new[] { Finding, Finding with { Severity = "fix" }, Finding with { Severity = "nit" } }
            .Select(f => ReviewNote.Comment(f).Split('\n')[0])
            .ShouldBe(
            [
                ":no_entry: **Blocking** · The log keeps the card number",
                ":wrench: **Fix** · The log keeps the card number",
                ":bulb: **Suggestion** · The log keeps the card number",
            ], "the severity in bold beside its shortcode, then the title");

    [Fact]
    public void What_Is_Wrong_Is_The_Findings_Why() =>
        ReviewNote.Comment(Finding).Split('\n')[2].ShouldBe("**What is wrong:** The card number is written to the log.", "what is wrong is the finding's why");

    [Fact]
    public void Why_It_Matters_Is_The_Findings_Context_And_Is_Left_Out_Without_One()
    {
        ReviewNote.Comment(Finding).Split('\n')[4].ShouldBe("**Why it matters:** A payment logs the whole request.", "why it matters is the finding's context");
        ReviewNote.Comment(Finding with { Context = null }).ShouldBe(
            ":no_entry: **Blocking** · The log keeps the card number\n\n**What is wrong:** The card number is written to the log.\n\n**Proposed fix:** Log the order id only.\n\n**Rule or document:** Never log a payment card.",
            "a finding without a context has no such line");
    }

    [Fact]
    public void The_Proposed_Fix_Is_The_Findings_Fix() =>
        ReviewNote.Comment(Finding).Split('\n')[6].ShouldBe("**Proposed fix:** Log the order id only.", "the proposed fix is the finding's fix");

    [Fact]
    public void The_Rule_Line_Is_Left_Out_When_The_Finding_Rests_On_No_Rule() =>
        ReviewNote.Comment(Finding with { Rule = null }).ShouldBe(
            ":no_entry: **Blocking** · The log keeps the card number\n\n**What is wrong:** The card number is written to the log.\n\n**Why it matters:** A payment logs the whole request.\n\n**Proposed fix:** Log the order id only.",
            "a finding that rests on no rule has no rule line");
}
