using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scout.Operator;

/// <summary>A stock picture saved for a scene, with its credit, or none, and the note the scene carries.</summary>
internal sealed record StockPicture(string? File, string? Credit, string Note);

/// <summary>
/// Finds a freely licensed picture for a scene on Openverse, without an account or a key: one search of
/// <c>https://api.openverse.org/v1/images/</c> for CC0, public domain mark and CC BY pictures, the first result of
/// an allowed licence that is a JPEG, PNG or WebP at least <see cref="MinWidth"/> pixels wide, downloaded with one GET
/// that accepts only JPEG, PNG and WebP as <c>scene-&lt;id&gt;.&lt;ext&gt;</c> into the media directory, refused unless its type is one of those and it holds at most
/// <see cref="MaxBytes"/>; result URLs must be https and no redirect is followed. The credit is Openverse's attribution, else one built from the title, creator and licence.
/// A search takes one of <see cref="PerDay"/> requests a day, counted in this process and reset at
/// midnight Pacific time, and starts at least <see cref="Interval"/> after the previous search, a 429 with a Retry-After waiting that long, at most <see cref="MaxRetryAfter"/>, before one retry; the download, from another host, is neither paced nor counted.
/// Each request, headers and body, ends after <see cref="SearchDeadline"/> for the search and <see cref="DownloadDeadline"/> for the picture. Every failure gives no picture and a note; nothing throws but a cancellation.
/// </summary>
internal sealed class OpenversePictures(IHttpClientFactory http, TimeProvider time)
{
    internal const string ClientName = "openverse";

    internal const string UserAgent = "Scout (+https://github.com/vitorafgomes/scout)";

    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal TimeSpan SearchDeadline { get; init; } = TimeSpan.FromSeconds(10);

    internal TimeSpan DownloadDeadline { get; init; } = TimeSpan.FromSeconds(60);

    internal const string SearchUrl = "https://api.openverse.org/v1/images/";

    internal const int PerDay = 100;

    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(3.1);

    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    internal const int MinWidth = 800;

    internal const long MaxBytes = 20L * 1024 * 1024;

    internal const int MaxQuery = 200;

    internal const string Spent = "The Openverse picture budget for today is spent; it resets at midnight Pacific time.";

    internal const string Found = "Picture from Openverse, credited in the video's description.";

    private const string Fallback = " The scene shows a text card; attach your own image in Scout Desktop, Production.";

    internal const string NoMatch = "No picture: Openverse has no CC0, public domain or CC BY picture for this scene." + Fallback;

    internal const string Unreachable = "No picture: Scout could not reach Openverse." + Fallback;

    internal const string Busy = "No picture: Openverse is limiting Scout's requests for now." + Fallback;

    private const string DownloadFailure = "Scout could not download the picture Openverse named as an image of at most 20 MB.";

    internal const string NotDownloaded = "No picture: " + DownloadFailure + Fallback;

    private static readonly string[] Licences = ["cc0", "pdm", "by"];

    private static readonly string[] Extensions = ["jpg", "jpeg", "png", "webp"];

    private static readonly string[] ContentTypes = ["image/jpeg", "image/png", "image/webp"];

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly SearchBudget _today = new(time, PerDay);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset _next = DateTimeOffset.MinValue;

    /// <summary>Find a picture for <paramref name="query"/> and save it in <paramref name="directory"/> for scene <paramref name="sceneId"/>.</summary>
    public async Task<StockPicture> FindAsync(string query, string directory, string sceneId, CancellationToken ct)
    {
        var terms = FilterText.Clean(query ?? string.Empty);
        if (terms.Length == 0)
        {
            return new StockPicture(null, null, NoMatch);
        }

        if (!_today.TryTake())
        {
            return new StockPicture(null, null, Spent);
        }

        var client = http.CreateClient(ClientName);
        Hit? hit;
        using var searchDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            using var response = await SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, SearchUri(terms)), searchDeadline, SearchDeadline, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new StockPicture(null, null, Busy);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new StockPicture(null, null, Unreachable);
            }

            var page = await JsonSerializer.DeserializeAsync<SearchPage>(
                await response.Content.ReadAsStreamAsync(searchDeadline.Token).ConfigureAwait(false), Json, searchDeadline.Token).ConfigureAwait(false);
            hit = (page?.Results ?? []).Select(Pick).FirstOrDefault(h => h is not null);
        }
        catch (Exception ex) when (Unanswered(ex, ct))
        {
            return new StockPicture(null, null, Unreachable);
        }

        if (hit is null)
        {
            return new StockPicture(null, null, NoMatch);
        }

        var file = $"scene-{sceneId}.{hit.Extension}";
        using var downloadDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        downloadDeadline.CancelAfter(DownloadDeadline);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, hit.Url);
            request.Headers.Accept.ParseAdd("image/*");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, downloadDeadline.Token).ConfigureAwait(false);
            var content = response.Content.Headers;
            if (!response.IsSuccessStatusCode
                || content.ContentType?.MediaType is not { } type
                || !ContentTypes.Contains(type.ToLowerInvariant())
                || content.ContentLength > MaxBytes)
            {
                return new StockPicture(null, null, NotDownloaded);
            }

            var body = await response.Content.ReadAsStreamAsync(downloadDeadline.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                await MediaFiles.RenderAsync(directory, file, DownloadFailure, temp => CopyAsync(body, temp, downloadDeadline.Token), downloadDeadline.Token).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex)
        {
            return new StockPicture(null, null, "No picture: " + ex.Message + Fallback);
        }
        catch (Exception ex) when (Unanswered(ex, ct) || ex is InvalidDataException)
        {
            return new StockPicture(null, null, NotDownloaded);
        }

        return new StockPicture(file, hit.Credit, Found);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client, Func<HttpRequestMessage> make, CancellationTokenSource deadline, TimeSpan limit, CancellationToken ct)
    {
        var response = await PacedAsync(client, make, null, deadline, limit, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.TooManyRequests || RetryAfter(response) is not { } hold)
        {
            return response;
        }

        response.Dispose();
        return await PacedAsync(client, make, hold, deadline, limit, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PacedAsync(
        HttpClient client, Func<HttpRequestMessage> make, TimeSpan? hold, CancellationTokenSource deadline, TimeSpan limit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            if (hold is { } wait && now + wait > _next)
            {
                _next = now + wait;
            }

            if (_next > now)
            {
                await Task.Delay(_next - now, time, ct).ConfigureAwait(false);
            }

            _next = time.GetUtcNow() + Interval;
        }
        finally
        {
            _gate.Release();
        }

        deadline.CancelAfter(limit);
        using var request = make();
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var seconds = header?.Delta ?? (header?.Date - time.GetUtcNow());
        return seconds is { } s ? TimeSpan.FromTicks(Math.Clamp(s.Ticks, 0, MaxRetryAfter.Ticks)) : null;
    }

    internal static Uri SearchUri(string terms)
    {
        var cut = terms.Length <= MaxQuery ? terms : terms[..(char.IsHighSurrogate(terms[MaxQuery - 1]) ? MaxQuery - 1 : MaxQuery)];
        return new($"{SearchUrl}?q={Uri.EscapeDataString(cut)}&license=cc0,by,pdm&mature=false&page_size=5");
    }

    private static bool Unanswered(Exception ex, CancellationToken ct)
        => ex is HttpRequestException or JsonException or IOException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static async Task CopyAsync(Stream body, string temp, CancellationToken ct)
    {
        var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using (target.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                {
                    throw new InvalidDataException(NotDownloaded);
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
    }

    private static Hit? Pick(SearchResult? result)
    {
        if (result is null
            || !Licences.Contains(result.License?.ToLowerInvariant())
            || result.Width is not >= MinWidth
            || !Uri.TryCreate(result.Url, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var extension = (string.IsNullOrWhiteSpace(result.Filetype) ? Path.GetExtension(url.AbsolutePath).TrimStart('.') : result.Filetype)
            .ToLowerInvariant();
        return Extensions.Contains(extension) ? new Hit(url, extension, Credit(result)) : null;
    }

    private static string Credit(SearchResult result)
    {
        var credit = string.IsNullOrWhiteSpace(result.Attribution)
            ? $"{Or(result.Title, "Untitled")} by {Or(result.Creator, "an unknown creator")}, {LicenceName(result.License!)} {result.LicenseVersion}".TrimEnd()
              + ", via Openverse"
            : result.Attribution;
        return FilterText.Clean(credit.Replace("<", string.Empty, StringComparison.Ordinal).Replace(">", string.Empty, StringComparison.Ordinal));
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string LicenceName(string license) => license.ToLowerInvariant() switch
    {
        "cc0" => "CC0",
        "pdm" => "Public Domain Mark",
        _ => "CC BY",
    };

    private sealed record Hit(Uri Url, string Extension, string Credit);

    private sealed record SearchPage(List<SearchResult?>? Results);

    private sealed record SearchResult(
        string? Url,
        string? Title,
        string? Creator,
        string? License,
        [property: JsonPropertyName("license_version")] string? LicenseVersion,
        string? Filetype,
        int? Width,
        string? Attribution);
}
