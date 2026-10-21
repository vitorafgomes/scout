namespace Scout.Plugin.CodeReview;

/// <summary>
/// Configuration for the Code Review plugin, bound from
/// "Scout:Plugins:CodeReview". The snippet-review functions need no
/// configuration. Repository analysis reuses the GitHub feature's token
/// ("Scout:Plugins:GitHub:Token"); no token is stored here.
/// </summary>
public sealed class CodeReviewOptions
{
    /// <summary>
    /// Repository URL used by repository analysis when the caller passes none.
    /// Empty disables the implicit default.
    /// </summary>
    public string DefaultRepoUrl { get; set; } = string.Empty;

    /// <summary>Sampling temperature forwarded to the model; null leaves the provider default.</summary>
    public double? Temperature { get; set; }

    /// <summary>Maximum completion tokens forwarded to the model; null leaves the provider default.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Per-file decoded-size cap in bytes; files larger than this are skipped.</summary>
    public int MaxFileBytes { get; set; } = 200_000;

    /// <summary>Maximum number of files pulled from a repository for a repository-level review.</summary>
    public int MaxFiles { get; set; } = 12;

    /// <summary>
    /// Overall character budget for the concatenated repository corpus sent to the
    /// model. The file set is truncated to fit so a repository review stays within
    /// a sane prompt size regardless of how many files were selected.
    /// </summary>
    public int CorpusCharBudget { get; set; } = 60_000;
}
