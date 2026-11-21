namespace Scout.Review;

/// <summary>Every limit of a finding and of a card, each written once.</summary>
public static class FindingLimits
{
    /// <summary>The findings a card holds at most.</summary>
    public const int MaxFindings = 50;

    /// <summary>The characters of a finding's id at most: letters, digits or hyphens.</summary>
    public const int MaxIdLength = 40;

    /// <summary>The characters of a finding's title at most.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>The characters of a finding's path at most.</summary>
    public const int MaxPathLength = 1000;

    /// <summary>The characters of a finding's rule at most, once trimmed.</summary>
    public const int MaxRuleLength = 300;

    /// <summary>The characters of a finding's why, fix and context at most, each.</summary>
    public const int MaxTextLength = 4000;

    /// <summary>The notes a card holds at most.</summary>
    public const int MaxNotes = 20;

    /// <summary>The characters of one note of a card at most.</summary>
    public const int MaxNoteLength = 300;

    /// <summary>
    /// The characters of a finding's evidence at most: a few lines of a patch, about eight lines of 120 characters, the
    /// smallest quote that proves a finding.
    /// </summary>
    public const int MaxEvidenceLength = 1000;
}
