using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scout.Plugin.Parcels.Abstractions;

namespace Scout.Plugin.Parcels;

/// <summary>
/// Thin read-only client over the Ship24 tracking API using an injected,
/// pre-configured <see cref="HttpClient"/> (base address, Bearer auth, timeout,
/// and User-Agent are set by DI). The lookup uses the tracking-search endpoint,
/// which returns results WITHOUT creating or subscribing a tracker on the
/// provider, so no external state is ever mutated (ADR descriptive/read-only).
/// Non-success responses and unreadable bodies become a user-safe
/// <see cref="ParcelsApiException"/>; timeouts and transport faults bubble
/// unchanged.
/// </summary>
internal sealed class ParcelsClient(HttpClient http) : IParcelsClient
{
    // Read-only search: gets tracking results without registering a tracker.
    private const string SearchPath = "public/v1/tracking/search";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<ParcelTracking?> TrackAsync(string trackingNumber, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SearchPath)
        {
            Content = JsonContent.Create(new SearchRequestDto(trackingNumber)),
        };

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Unknown tracking number / not in the carrier network.
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ToApiException(response);
        }

        try
        {
            var payload = await response.Content
                .ReadFromJsonAsync<SearchResponseDto>(cancellationToken: ct).ConfigureAwait(false);
            var tracking = payload?.Data?.Trackings?.FirstOrDefault();
            return tracking is null ? null : Map(tracking, trackingNumber);
        }
        catch (JsonException)
        {
            throw new ParcelsApiException(
                "The parcel tracking service returned an unreadable response.");
        }
    }

    private static ParcelTracking Map(TrackingDto tracking, string requestedNumber)
    {
        var number = NullIfBlank(tracking.Tracker?.TrackingNumber) ?? requestedNumber;
        var status = NullIfBlank(tracking.Shipment?.StatusMilestone) ?? "unknown";
        var estimated = NullIfBlank(tracking.Shipment?.Delivery?.EstimatedDeliveryDate);
        var events = (tracking.Events ?? [])
            .Select(e => new TrackingEvent(
                NullIfBlank(e.OccurrenceDatetime) ?? string.Empty,
                NullIfBlank(e.Status) ?? "Update",
                NullIfBlank(e.Location)))
            .ToList();

        return new ParcelTracking(number, status, estimated, events);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static ParcelsApiException ToApiException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                return new ParcelsApiException(
                    "Parcel tracking authentication failed. Check the configured API key.", status);
            case HttpStatusCode.TooManyRequests:
                return new ParcelsApiException(
                    "The parcel tracking service is rate limiting requests. Try again later.", status);
            default:
                if ((int)status >= 500)
                {
                    return new ParcelsApiException(
                        "The parcel tracking service is currently unavailable. Try again later.", status);
                }

                return new ParcelsApiException(
                    $"The parcel tracking request failed ({(int)status}).", status);
        }
    }

    private sealed record SearchRequestDto(
        [property: JsonPropertyName("trackingNumber")] string TrackingNumber);

    private sealed record SearchResponseDto(
        [property: JsonPropertyName("data")] SearchDataDto? Data);

    private sealed record SearchDataDto(
        [property: JsonPropertyName("trackings")] List<TrackingDto>? Trackings);

    private sealed record TrackingDto(
        [property: JsonPropertyName("tracker")] TrackerDto? Tracker,
        [property: JsonPropertyName("shipment")] ShipmentDto? Shipment,
        [property: JsonPropertyName("events")] List<EventDto>? Events);

    private sealed record TrackerDto(
        [property: JsonPropertyName("trackingNumber")] string? TrackingNumber);

    private sealed record ShipmentDto(
        [property: JsonPropertyName("statusMilestone")] string? StatusMilestone,
        [property: JsonPropertyName("delivery")] DeliveryDto? Delivery);

    private sealed record DeliveryDto(
        [property: JsonPropertyName("estimatedDeliveryDate")] string? EstimatedDeliveryDate);

    private sealed record EventDto(
        [property: JsonPropertyName("occurrenceDatetime")] string? OccurrenceDatetime,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("location")] string? Location);
}
