using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Scout.Plugin.CloudStatus.Providers.Oci;

/// <summary>
/// Default <see cref="IOciHealthSource"/>. GETs the configured OCI status
/// endpoint and parses the JSON response into <see cref="OciHealthIncident"/>
/// values. The Oracle Cloud status page is public — no authentication.
///
/// <para>
/// Exceptions pass through: the provider maps <see cref="HttpRequestException"/>,
/// <see cref="TaskCanceledException"/>, and anything else to
/// <see cref="Domain.HealthLevel.Unknown"/>.
/// </para>
/// </summary>
public sealed class OciHealthSource : IOciHealthSource
{
    private readonly HttpClient _http;
    private readonly OciCloudHealthOptions _options;
    private readonly HashSet<string> _regionFilter;

    public OciHealthSource(HttpClient http, IOptions<OciCloudHealthOptions> options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value);
        _options = options.Value;

        if (_http.Timeout == Timeout.InfiniteTimeSpan || _http.Timeout == TimeSpan.Zero)
        {
            _http.Timeout = _options.Timeout;
        }

        _regionFilter = new HashSet<string>(
            _options.Regions ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<OciHealthIncident>> GetActiveIncidentsAsync(
        CancellationToken cancellationToken)
    {
        var payload = await _http
            .GetFromJsonAsync<OciStatusPayload>(_options.StatusEndpoint, cancellationToken)
            .ConfigureAwait(false);

        if (payload?.Incidents is null || payload.Incidents.Count == 0)
        {
            return Array.Empty<OciHealthIncident>();
        }

        var results = new List<OciHealthIncident>(payload.Incidents.Count);
        foreach (var incident in payload.Incidents)
        {
            if (incident is null)
            {
                continue;
            }

            // Skip resolved incidents — only active status matters.
            if (!string.Equals(incident.Status, "resolved", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(incident.Status, "postmortem", StringComparison.OrdinalIgnoreCase))
            {
                var region = incident.Region ?? "(global)";
                if (_regionFilter.Count > 0 && !_regionFilter.Contains(region))
                {
                    continue;
                }

                results.Add(new OciHealthIncident(
                    Id: incident.Id ?? Guid.NewGuid().ToString(),
                    Service: incident.Service ?? "OracleCloud",
                    Region: region,
                    Impact: incident.Impact ?? "unknown",
                    Title: incident.Name ?? "(untitled incident)"));
            }
        }
        return results;
    }

    // JSON shape we accept — permissive, maps Oracle's observed fields and
    // tolerates missing properties gracefully. If Oracle changes the schema,
    // this is the only file that needs to move.
    private sealed class OciStatusPayload
    {
        [JsonPropertyName("incidents")]
        public List<OciIncidentDto>? Incidents { get; set; }
    }

    private sealed class OciIncidentDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("impact")]
        public string? Impact { get; set; }

        [JsonPropertyName("service")]
        public string? Service { get; set; }

        [JsonPropertyName("region")]
        public string? Region { get; set; }
    }
}
