using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.News.Abstractions;

namespace Scout.Plugin.News;

/// <summary>
/// News plugin exposed to the LLM as Semantic Kernel functions. Headlines come
/// from operator-configured RSS/Atom feeds and from the public Hacker News Algolia
/// API - both read-only GETs, nothing external is ever mutated. The only write is
/// descriptive: links saved to Scout's own local reading list (ADR-allowed). Feed
/// and story titles are untrusted external content, so they are collapsed to a
/// single line and length-capped before being relayed as inert data. Every call
/// returns a non-empty, user-safe string; invalid model input yields a corrective
/// sentence and transport, API, or storage failures are caught and surfaced as a
/// clean error line, so a tool call never throws back into the kernel.
/// </summary>
public sealed class NewsPlugin
{
    private const string Collection = "reading-list";
    private const int MaxLines = 10;
    private const int MaxTitleLength = 150;
    private const int MaxUrlLength = 200;

    private readonly IFeedClient _feedClient;
    private readonly IHackerNewsClient _hackerNews;
    private readonly IScoutLocalStore _store;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string> _feeds;
    private readonly IReadOnlyList<string> _feedAliases;

    /// <summary>Creates the plugin over the two news clients, the local store, and a clock.</summary>
    /// <param name="feedClient">RSS/Atom feed client. Must be non-null.</param>
    /// <param name="hackerNews">Hacker News client. Must be non-null.</param>
    /// <param name="store">The Scout-local store for the reading list. Must be non-null.</param>
    /// <param name="clock">Clock used to stamp reading-list saves. Must be non-null.</param>
    /// <param name="options">Plugin options supplying the feed alias map. Must be non-null.</param>
    public NewsPlugin(
        IFeedClient feedClient,
        IHackerNewsClient hackerNews,
        IScoutLocalStore store,
        TimeProvider clock,
        NewsOptions options)
    {
        _feedClient = feedClient ?? throw new ArgumentNullException(nameof(feedClient));
        _hackerNews = hackerNews ?? throw new ArgumentNullException(nameof(hackerNews));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);

        var (feeds, aliases) = BuildFeeds(options.Feeds);
        _feeds = feeds;
        _feedAliases = aliases;
    }

    /// <summary>List the latest headlines from a configured news feed.</summary>
    /// <param name="feed">Alias of a configured feed, or empty for the default.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Up to ten headlines, one per line, or a corrective or clean message.</returns>
    [KernelFunction("get_top_headlines")]
    [Description("List the latest headlines from a configured news feed, one per line with title and link.")]
    public async Task<string> GetTopHeadlinesAsync(
        [Description("Name of a configured feed, e.g. \"bbc\". Leave empty to use the default feed.")] string feed = "",
        CancellationToken ct = default)
    {
        if (!TryResolveFeed(feed, out var alias, out var url, out var hint))
        {
            return hint;
        }

        try
        {
            var items = await _feedClient.GetFeedAsync(url, ct).ConfigureAwait(false);
            if (items.Count == 0)
            {
                return $"No headlines found in the {alias} feed.";
            }

            var lines = items.Take(MaxLines).Select(FormatFeedItem);
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Search current news stories on Hacker News for a term.</summary>
    /// <param name="query">Word or phrase to search for.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Up to ten matches, one per line, a no-match sentence, a corrective hint, or a clean error.</returns>
    [KernelFunction("search_news")]
    [Description("Search current news stories on Hacker News for a term and list the top matches, one per line.")]
    public async Task<string> SearchNewsAsync(
        [Description("Word or phrase to search for, e.g. \"artificial intelligence\".")] string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Provide a search term, for example: search_news query=\"open source\".";
        }

        try
        {
            var stories = await _hackerNews.SearchAsync(query.Trim(), ct).ConfigureAwait(false);
            if (stories.Count == 0)
            {
                return $"No news stories found for \"{query.Trim()}\".";
            }

            var lines = stories.Take(MaxLines).Select(FormatStory);
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>List the current top stories on Hacker News.</summary>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Up to ten stories, one per line, or a clean message.</returns>
    [KernelFunction("get_hacker_news_top")]
    [Description("List the current top stories on Hacker News, one per line with points and link.")]
    public async Task<string> GetHackerNewsTopAsync(CancellationToken ct = default)
    {
        try
        {
            var stories = await _hackerNews.GetTopStoriesAsync(ct).ConfigureAwait(false);
            if (stories.Count == 0)
            {
                return "No Hacker News stories available right now. Try again later.";
            }

            var lines = stories.Take(MaxLines).Select(FormatStory);
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Save a link to the local reading list for later.</summary>
    /// <param name="url">The absolute http(s) link to save.</param>
    /// <param name="title">Optional title for the link.</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation line, a corrective hint on invalid input, or a clean error line.</returns>
    [KernelFunction("save_to_reading_list")]
    [Description("Save a link to Scout's local reading list for later, with an optional title. Writes only to local storage.")]
    public async Task<string> SaveToReadingListAsync(
        [Description("The link to save, e.g. \"https://example.com/article\".")] string url,
        [Description("Optional title for the link, e.g. \"Great article on AI\". Leave empty to omit.")] string title = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "Provide a link to save, for example: save_to_reading_list url=\"https://example.com/article\".";
        }

        var link = url.Trim();
        if (!IsValidHttpUrl(link))
        {
            return "Provide a valid http(s) link, for example: https://example.com/article.";
        }

        try
        {
            var cleanTitle = Clean(title, MaxTitleLength);
            var id = string.Empty;
            await _store.MutateAsync<ReadingListDocument>(Collection, doc =>
            {
                id = "r" + doc.NextId.ToString(CultureInfo.InvariantCulture);
                doc.NextId++;
                doc.Items.Add(new ReadingListEntry
                {
                    Id = id,
                    Url = link,
                    Title = cleanTitle,
                    SavedUtc = _clock.GetUtcNow(),
                });
            }, ct).ConfigureAwait(false);

            return $"Saved {id} to your reading list.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>List the links saved in the local reading list, newest first.</summary>
    /// <param name="ct">Cancels the underlying store read.</param>
    /// <returns>Up to ten saved links, one per line, an empty sentence, or a clean error line.</returns>
    [KernelFunction("get_reading_list")]
    [Description("List the links saved in Scout's local reading list, newest first, one per line.")]
    public async Task<string> GetReadingListAsync(CancellationToken ct = default)
    {
        try
        {
            var document = await _store.ReadOrCreateAsync<ReadingListDocument>(Collection, ct).ConfigureAwait(false);
            if (document.Items.Count == 0)
            {
                return "Your reading list is empty.";
            }

            var recent = document.Items.AsEnumerable().Reverse().Take(MaxLines).Select(FormatEntry);
            return string.Join("\n", recent);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private bool TryResolveFeed(string feed, out string alias, out string url, out string hint)
    {
        alias = string.Empty;
        url = string.Empty;
        hint = string.Empty;

        if (_feedAliases.Count == 0)
        {
            hint = "No news feeds are configured. Ask the operator to add feeds under Scout:Plugins:News:Feeds.";
            return false;
        }

        var wanted = string.IsNullOrWhiteSpace(feed) ? _feedAliases[0] : feed.Trim();
        if (!_feeds.TryGetValue(wanted, out var resolved))
        {
            hint = $"Unknown feed \"{wanted}\". Available feeds: {string.Join(", ", _feedAliases)}.";
            return false;
        }

        alias = wanted;
        url = resolved;
        return true;
    }

    private static string FormatFeedItem(FeedItem item)
    {
        var title = Clean(item.Title, MaxTitleLength);
        return string.IsNullOrWhiteSpace(item.Link)
            ? title
            : $"{title} - {CleanLink(item.Link, MaxUrlLength)}";
    }

    private static string FormatStory(HackerNewsStory story)
    {
        var title = Clean(story.Title, MaxTitleLength);
        var points = story.Points == 1 ? "1 point" : $"{story.Points} points";
        return $"{title} ({points}) - {CleanLink(story.Url, MaxUrlLength)}";
    }

    private static string FormatEntry(ReadingListEntry entry)
    {
        var date = entry.SavedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var title = string.IsNullOrWhiteSpace(entry.Title) ? "(no title)" : entry.Title;
        return $"{entry.Id}  {date}  {title}  {CleanLink(entry.Url, MaxUrlLength)}";
    }

    private static (IReadOnlyDictionary<string, string> Feeds, IReadOnlyList<string> Aliases) BuildFeeds(
        Dictionary<string, string>? source)
    {
        var feeds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<string>();
        if (source is null)
        {
            return (feeds, aliases);
        }

        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var alias = key.Trim();
            if (feeds.ContainsKey(alias))
            {
                continue;
            }

            feeds[alias] = value.Trim();
            aliases.Add(alias);
        }

        return (feeds, aliases);
    }

    private static bool IsValidHttpUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // Collapse untrusted external text to a single line and length-cap it, so
    // headlines are relayed strictly as inert data.
    private static string Clean(string? raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var collapsed = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= max ? collapsed : collapsed[..max] + "...";
    }

    // Untrusted external links are relayed as inert data: a valid URL carries no
    // whitespace or control characters, so strip any (line breaks included) before
    // length-capping. This stops a crafted feed link or story URL from smuggling a
    // newline that would fake an extra line in the one-item-per-line output.
    private static string CleanLink(string value, int max)
    {
        var stripped = string.Concat(value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)));
        return stripped.Length <= max ? stripped : stripped[..max];
    }

    private static bool IsHandledFailure(Exception ex)
        => ex is NewsApiException
            or HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        NewsApiException api => api.Message,
        OperationCanceledException => "The news request timed out. Try again later.",
        IOException or UnauthorizedAccessException => "Could not access the local reading list. Try again.",
        _ => "Could not reach the news service. Try again later.",
    };
}
