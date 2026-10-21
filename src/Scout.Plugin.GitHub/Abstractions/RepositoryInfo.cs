namespace Scout.Plugin.GitHub.Abstractions;

/// <summary>
/// Overview of a single repository for describing or summarizing a project.
/// Read-only projection sourced from the GitHub REST API. Optional parts
/// (README, contents) may be absent when the sub-call fails or returns nothing.
/// </summary>
/// <param name="FullName">Owner-qualified name, e.g. "octocat/hello-world".</param>
/// <param name="Description">Short project description, or null when unset.</param>
/// <param name="Language">Primary language, or null when undetected.</param>
/// <param name="Languages">Language breakdown by byte count, descending; may be empty.</param>
/// <param name="Topics">Repository topics; may be empty.</param>
/// <param name="License">SPDX license id, or null when none.</param>
/// <param name="DefaultBranch">Default branch name, e.g. "main".</param>
/// <param name="StarCount">Stargazer count.</param>
/// <param name="ForkCount">Fork count.</param>
/// <param name="HtmlUrl">Browser URL for the repository.</param>
/// <param name="Readme">Decoded README text (truncated), or null when absent.</param>
/// <param name="TopLevelEntries">Root entries as "name (type)", e.g. "src (dir)"; may be empty.</param>
public sealed record RepositoryInfo(
    string FullName,
    string? Description,
    string? Language,
    IReadOnlyList<LanguageUsage> Languages,
    IReadOnlyList<string> Topics,
    string? License,
    string DefaultBranch,
    int StarCount,
    int ForkCount,
    string HtmlUrl,
    string? Readme,
    IReadOnlyList<string> TopLevelEntries);

/// <summary>A language and the number of bytes it occupies in the repository.</summary>
/// <param name="Name">Language name, e.g. "C#".</param>
/// <param name="Bytes">Bytes of source attributed to this language.</param>
public sealed record LanguageUsage(string Name, long Bytes);
