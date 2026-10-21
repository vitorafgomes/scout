using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using Scout.Plugin.News.Abstractions;

namespace Scout.Plugin.News;

/// <summary>
/// Thin read-only client over a single RSS or Atom feed using an injected,
/// pre-configured <see cref="HttpClient"/> (timeout and User-Agent are set by DI).
/// Feed URLs are absolute and come from operator configuration, never the model.
/// Non-success responses and unreadable bodies become a user-safe
/// <see cref="NewsApiException"/>; timeouts and transport faults bubble unchanged.
/// Every call is a GET.
/// </summary>
internal sealed class FeedClient(HttpClient http) : IFeedClient
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<IReadOnlyList<FeedItem>> GetFeedAsync(string feedUrl, CancellationToken ct)
    {
        using var response = await _http.GetAsync(feedUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Parse(xml);
    }

    // Parse both RSS (<channel><item>) and Atom (<feed><entry>) by matching on
    // element local names, so namespace prefixes never break parsing.
    private static IReadOnlyList<FeedItem> Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            throw new NewsApiException("The news feed returned an unreadable response.");
        }

        var root = document.Root
            ?? throw new NewsApiException("The news feed returned an empty response.");

        var isAtom = string.Equals(root.Name.LocalName, "feed", StringComparison.OrdinalIgnoreCase);
        var itemName = isAtom ? "entry" : "item";

        var items = new List<FeedItem>();
        foreach (var element in root.Descendants()
                     .Where(e => string.Equals(e.Name.LocalName, itemName, StringComparison.OrdinalIgnoreCase)))
        {
            var title = LocalValue(element, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var link = isAtom ? AtomLink(element) : LocalValue(element, "link");
            var dateText = isAtom
                ? LocalValue(element, "published") ?? LocalValue(element, "updated")
                : LocalValue(element, "pubDate");

            items.Add(new FeedItem(title.Trim(), (link ?? string.Empty).Trim(), ParseDate(dateText)));
        }

        return items;
    }

    private static string? LocalValue(XElement parent, string localName)
        => parent.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    // Atom entries can carry several <link> elements; prefer the alternate (or
    // unlabelled) one and read its href attribute.
    private static string AtomLink(XElement entry)
    {
        var links = entry.Elements()
            .Where(e => string.Equals(e.Name.LocalName, "link", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var alternate = links.FirstOrDefault(l =>
        {
            var rel = (string?)l.Attribute("rel");
            return rel is null || string.Equals(rel, "alternate", StringComparison.OrdinalIgnoreCase);
        });

        var chosen = alternate ?? links.FirstOrDefault();
        return chosen?.Attribute("href")?.Value ?? string.Empty;
    }

    private static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // RSS pubDate carries a weekday name (e.g. "Wed, ") that some feeds get
        // wrong; the ddd token would reject a mismatched weekday, so drop a leading
        // "Ddd, " prefix before parsing. Atom uses ISO 8601 and has no such prefix.
        var trimmed = text.Trim();
        var comma = trimmed.IndexOf(", ", StringComparison.Ordinal);
        if (comma is > 0 and <= 4)
        {
            trimmed = trimmed[(comma + 2)..];
        }

        return DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var parsed)
            ? parsed
            : null;
    }

    private static NewsApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.NotFound:
                return new NewsApiException(
                    "The news feed could not be found. Check the configured feed URL.", status);
            case HttpStatusCode.TooManyRequests:
                return new NewsApiException(
                    "The news feed is rate limiting requests. Try again later.", status);
            default:
                if ((int)status >= 500)
                {
                    return new NewsApiException(
                        "The news feed is currently unavailable. Try again later.", status);
                }

                return new NewsApiException(
                    $"The news feed request failed ({(int)status}).", status);
        }
    }
}
