namespace Scout.Plugin.WhatToWatch.Abstractions;

/// <summary>
/// Read-only access to the TVMaze show database. Every operation is a GET; no
/// external state is ever mutated. Available without any API key.
/// </summary>
public interface ITvMazeClient
{
    /// <summary>
    /// Shows whose title matches <paramref name="title"/>, best matches first.
    /// </summary>
    Task<IReadOnlyList<Show>> SearchShowsAsync(string title, CancellationToken ct);

    /// <summary>
    /// The best-matching show for <paramref name="title"/> together with its next
    /// scheduled episode. Returns null when no show matches.
    /// </summary>
    Task<ShowEpisode?> GetNextEpisodeAsync(string title, CancellationToken ct);

    /// <summary>
    /// Highly rated shows tagged with <paramref name="genre"/> (case-insensitive),
    /// best rated first.
    /// </summary>
    Task<IReadOnlyList<Show>> RecommendShowsAsync(string genre, CancellationToken ct);
}
