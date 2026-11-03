using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.YouTube.Abstractions;

namespace Scout.Plugin.YouTube;

/// <summary>
/// The YouTube Reporting API over an injected <see cref="HttpClient"/> whose base address is
/// <c>https://youtubereporting.googleapis.com/v1/</c> and whose handler sets the owner's bearer token: the owner's reach
/// reporting job and its daily CSV reports. A download address outside the Reporting API is refused before any request,
/// so the token never goes elsewhere; a report missing a column or holding a value that is not a number fails whole. The
/// click-through rate is documented as clicks divided by impressions but named a percentage, so a report holding any rate
/// above 1 is read as percent and every rate in it divided by 100.
/// </summary>
internal sealed class YouTubeReportingClient(HttpClient http) : IYouTubeReportingClient
{
    internal const string ReachReportType = "channel_reach_basic_a1";

    internal const string UntrustedDownload = "The reach report address is not YouTube's Reporting API; Scout did not download it.";

    internal const string MalformedReport = "YouTube returned a reach report Scout could not read.";

    private const string ReportingHost = "youtubereporting.googleapis.com";

    public async Task<string?> FindReachJobAsync(CancellationToken ct)
    {
        var page = await GetAsync<JobsDto>("jobs", ct).ConfigureAwait(false);
        return page.Jobs?.FirstOrDefault(j => j.ReportTypeId == ReachReportType)?.Id;
    }

    public async Task<string> CreateReachJobAsync(CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("jobs", new JobDto(null, ReachReportType, "Scout reach"), ct).ConfigureAwait(false);
        var job = await ReadAsync<JobDto>(response, ct).ConfigureAwait(false);
        return job.Id ?? throw new YouTubeApiException("YouTube returned an unexpected response.");
    }

    public async Task<IReadOnlyList<YouTubeReport>> ListReportsAsync(string jobId, DateTimeOffset createdAfter, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var path = $"jobs/{Uri.EscapeDataString(jobId)}/reports?createdAfter="
            + Uri.EscapeDataString(createdAfter.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        var reports = new List<YouTubeReport>();
        string? token = null;
        do
        {
            var page = await GetAsync<ReportsDto>(token is null ? path : $"{path}&pageToken={Uri.EscapeDataString(token)}", ct)
                .ConfigureAwait(false);
            reports.AddRange((page.Reports ?? [])
                .Where(r => r.Id is not null && r.DownloadUrl is not null && r.StartTime is not null && r.EndTime is not null)
                .Select(r => new YouTubeReport(r.Id!, r.StartTime!.Value, r.EndTime!.Value, r.DownloadUrl!, r.CreateTime)));
            token = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(token));

        return reports;
    }

    public async Task<IReadOnlyList<YouTubeReachRow>> DownloadReachAsync(string downloadUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps
            || address.Host != ReportingHost)
        {
            throw new YouTubeApiException(UntrustedDownload);
        }

        using var response = await http.GetAsync(address, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await YouTubeClient.ToApiExceptionAsync(response, mine: false, ct).ConfigureAwait(false);
        }

        return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static List<YouTubeReachRow> Parse(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            throw new YouTubeApiException(MalformedReport);
        }

        var header = lines[0].Split(',').ToList();
        int Column(string name) => header.IndexOf(name) is >= 0 and var index ? index : throw new YouTubeApiException(MalformedReport);
        var (date, video, impressions, ctr) = (Column("date"), Column("video_id"), Column("video_thumbnail_impressions"), Column("video_thumbnail_impressions_ctr"));
        List<YouTubeReachRow> rows = [.. lines.Skip(1).Select(line =>
        {
            var cells = line.Split(',');
            return cells.Length == header.Count
                && DateOnly.TryParseExact(cells[date], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && long.TryParse(cells[impressions], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                && double.TryParse(cells[ctr], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
                    ? new YouTubeReachRow(day, cells[video], count, rate)
                    : throw new YouTubeApiException(MalformedReport);
        })];
        return rows.Any(r => r.ImpressionsCtr > 1)
            ? [.. rows.Select(r => r with { ImpressionsCtr = r.ImpressionsCtr / 100 })]
            : rows;
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await YouTubeClient.ToApiExceptionAsync(response, mine: false, ct).ConfigureAwait(false);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false)
                ?? throw new YouTubeApiException("YouTube returned an unexpected empty response.");
        }
        catch (JsonException)
        {
            throw new YouTubeApiException("YouTube returned an unexpected response.");
        }
    }

    private sealed record JobsDto([property: JsonPropertyName("jobs")] List<JobDto>? Jobs);

    private sealed record JobDto(
        [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id,
        [property: JsonPropertyName("reportTypeId")] string? ReportTypeId,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record ReportsDto(
        [property: JsonPropertyName("reports")] List<ReportDto>? Reports,
        [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

    private sealed record ReportDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("startTime")] DateTimeOffset? StartTime,
        [property: JsonPropertyName("endTime")] DateTimeOffset? EndTime,
        [property: JsonPropertyName("downloadUrl")] string? DownloadUrl,
        [property: JsonPropertyName("createTime")] DateTimeOffset? CreateTime);
}
