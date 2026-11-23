namespace Scout.Review.Tests;

/// <summary>Every limit of a finding and of a card has one value, the one the desktop and the worker agree on.</summary>
public sealed class FindingLimitsTests
{
    [Fact]
    public void Every_Limit_Of_A_Finding_And_A_Card_Has_One_Value()
    {
        int[] limits =
        [
            FindingLimits.MaxFindings,
            FindingLimits.MaxIdLength,
            FindingLimits.MaxTitleLength,
            FindingLimits.MaxPathLength,
            FindingLimits.MaxRuleLength,
            FindingLimits.MaxTextLength,
            FindingLimits.MaxNotes,
            FindingLimits.MaxNoteLength,
            FindingLimits.MaxEvidenceLength,
        ];

        limits.ShouldBe(
            [50, 40, 200, 1000, 300, 4000, 20, 300, 1000],
            "findings 50, id 40, title 200, path 1000, rule 300, why, fix and context 4000, notes 20 of 300 characters each, evidence 1000");
    }
}
