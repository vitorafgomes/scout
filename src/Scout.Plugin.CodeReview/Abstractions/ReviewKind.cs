namespace Scout.Plugin.CodeReview.Abstractions;

/// <summary>
/// The kind of review requested. Selects the system prompt and the way the
/// user-supplied content is framed for the model.
/// </summary>
public enum ReviewKind
{
    /// <summary>General code-quality assessment of a snippet.</summary>
    Quality,

    /// <summary>Security vulnerability detection on a snippet.</summary>
    Vulnerabilities,

    /// <summary>Concrete improvement suggestions for a snippet.</summary>
    Improvements,

    /// <summary>Unit-test generation for a snippet.</summary>
    Tests,

    /// <summary>Plain-language explanation of a snippet.</summary>
    Explain,

    /// <summary>Review of a pull request supplied as a unified diff.</summary>
    PullRequest,

    /// <summary>Repository-level review over a bounded set of key files.</summary>
    Repository,
}
