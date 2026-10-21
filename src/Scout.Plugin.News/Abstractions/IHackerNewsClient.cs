namespace Scout.Plugin.News.Abstractions;

/// <summary>
/// Read-only access to Hacker News via the public Algolia API. Every operation is
/// a GET; no external state is ever mutated.
/// </summary>
public interface IHackerNewsClient
{
    /// <summary>The current Hacker News front-page stories, most prominent first.</summary>
    Task<IReadOnlyList<HackerNewsStory>> GetTopStoriesAsync(CancellationToken ct);

    /// <summary>Stories matching <paramref name="query"/>, most relevant first.</summary>
    Task<IReadOnlyList<HackerNewsStory>> SearchAsync(string query, CancellationToken ct);
}
