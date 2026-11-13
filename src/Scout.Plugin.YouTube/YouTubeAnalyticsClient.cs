using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Plugin.YouTube;

/// <summary>
/// The YouTube Analytics API v2 <c>reports.query</c> over an injected <see cref="HttpClient"/> whose base address is
/// <c>https://youtubeanalytics.googleapis.com/v2/</c> and whose handler sets the owner's bearer token. Columns are read by
/// name, never by position.
/// </summary>
internal sealed class YouTubeAnalyticsClient(HttpClient http) : IYouTubeAnalyticsClient
{
    internal const string Unexpected = "YouTube Analytics returned a report Scout could not read.";

    private const string Metrics =
        "views,estimatedMinutesWatched,averageViewDuration,averageViewPercentage,subscribersGained,subscribersLost,likes,comments,shares";

    public async Task<YouTubeVideoReport?> ReportAsync(string videoId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var table = await QueryAsync($"{Range(start, end)}&metrics={Metrics}&filters=video=={Uri.EscapeDataString(videoId)}", ct)
            .ConfigureAwait(false);
        if (table.Rows.Count == 0)
        {
            return null;
        }

        var row = table.Rows[0];
        return new YouTubeVideoReport(
            (long)table.Number(row, "views"),
            table.Number(row, "estimatedMinutesWatched"),
            table.Number(row, "averageViewDuration"),
            table.Number(row, "averageViewPercentage"),
            (long)table.Number(row, "subscribersGained"),
            (long)table.Number(row, "subscribersLost"),
            (long)table.Number(row, "likes"),
            (long)table.Number(row, "comments"),
            (long)table.Number(row, "shares"));
    }

    public async Task<IReadOnlyList<YouTubeRetentionPoint>> RetentionAsync(string videoId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var table = await QueryAsync(
            $"{Range(start, end)}&metrics=audienceWatchRatio&dimensions=elapsedVideoTimeRatio&filters=video=={Uri.EscapeDataString(videoId)}",
            ct).ConfigureAwait(false);
        return [.. table.Rows.Select(row => new YouTubeRetentionPoint(
            table.Number(row, "elapsedVideoTimeRatio"),
            table.Number(row, "audienceWatchRatio")))];
    }

    private static string Range(DateOnly start, DateOnly end)
        => "ids=channel==MINE"
            + $"&startDate={start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            + $"&endDate={end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    private async Task<Table> QueryAsync(string query, CancellationToken ct)
    {
        using var response = await http.GetAsync($"reports?{query}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await YouTubeClient.ToApiExceptionAsync(response, mine: false, ct).ConfigureAwait(false);
        }

        try
        {
            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(ct).ConfigureAwait(false)
                ?? throw new YouTubeApiException(Unexpected);
            var root = document.RootElement;
            var columns = root.TryGetProperty("columnHeaders", out var headers)
                ? headers.EnumerateArray().Select(h => h.GetProperty("name").GetString() ?? string.Empty).ToList()
                : [];
            var rows = root.TryGetProperty("rows", out var found) && found.ValueKind == JsonValueKind.Array
                ? found.EnumerateArray().Select(r => r.EnumerateArray().Select(Value).ToList()).ToList()
                : [];
            return new Table(columns, rows);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new YouTubeApiException(Unexpected);
        }
    }

    private static double Value(JsonElement cell)
        => cell.ValueKind == JsonValueKind.Number ? cell.GetDouble() : throw new YouTubeApiException(Unexpected);

    private sealed record Table(List<string> Columns, List<List<double>> Rows)
    {
        public double Number(List<double> row, string column)
        {
            var index = Columns.IndexOf(column);
            return index >= 0 && index < row.Count ? row[index] : throw new YouTubeApiException(Unexpected);
        }
    }
}
