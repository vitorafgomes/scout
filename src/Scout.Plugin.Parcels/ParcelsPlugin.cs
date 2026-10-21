using System.ComponentModel;
using System.Globalization;
using Microsoft.SemanticKernel;
using Scout.Plugin.Common.Abstractions;
using Scout.Plugin.Parcels.Abstractions;

namespace Scout.Plugin.Parcels;

/// <summary>
/// Parcel tracking exposed to the LLM as Semantic Kernel functions. Reads are
/// look-ups against the provider that never create or subscribe a tracker; the
/// watch list is descriptive-write only, stored in the Scout-local
/// "watched-packages" collection and nothing external. Invalid model input
/// returns a short corrective sentence, and transport, API, or storage failures
/// are caught and surfaced as a clean error line, so a tool call never throws
/// back into the kernel.
/// </summary>
public sealed class ParcelsPlugin
{
    private const string Collection = "watched-packages";
    private const string TrackingHint =
        "Provide a tracking number like LX123456789GB or 1Z999AA10123456784.";

    private const int MaxEvents = 5;

    private readonly IParcelsClient _client;
    private readonly IScoutLocalStore _store;
    private readonly TimeProvider _clock;
    private readonly string _timeZone;

    /// <summary>Creates the plugin over a read-only client, the local store, and a clock.</summary>
    /// <param name="client">The read-only parcel tracking client. Must be non-null.</param>
    /// <param name="store">The Scout-local store backing the watch list. Must be non-null.</param>
    /// <param name="clock">Clock used to stamp when a package is added. Must be non-null.</param>
    /// <param name="options">Plugin options supplying the display time zone. Must be non-null.</param>
    public ParcelsPlugin(
        IParcelsClient client,
        IScoutLocalStore store,
        TimeProvider clock,
        ParcelsOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _timeZone = string.IsNullOrWhiteSpace(options.TimeZone) ? "Europe/London" : options.TimeZone;
    }

    /// <summary>Look up the current status and latest movements of a parcel.</summary>
    /// <param name="trackingNumber">The carrier tracking number.</param>
    /// <param name="ct">Cancels the underlying request.</param>
    /// <returns>Status plus recent events, a not-found line, a corrective hint, or a clean error line.</returns>
    [KernelFunction("track_package")]
    [Description("Look up the current status and latest movements of a parcel by its tracking number. Read-only: does not register the parcel anywhere.")]
    public async Task<string> TrackPackageAsync(
        [Description("The carrier tracking number, e.g. \"LX123456789GB\".")] string trackingNumber,
        CancellationToken ct = default)
    {
        var number = Normalize(trackingNumber);
        if (!IsValidTrackingNumber(number))
        {
            return TrackingHint;
        }

        try
        {
            var tracking = await _client.TrackAsync(number, ct).ConfigureAwait(false);
            return tracking is null
                ? $"No tracking information found for {number}."
                : FormatTracking(tracking);
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Add a parcel to the Scout-local watch list.</summary>
    /// <param name="trackingNumber">The carrier tracking number to watch.</param>
    /// <param name="label">Optional friendly label.</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation, an already-watching note, a corrective hint, or a clean error line.</returns>
    [KernelFunction("watch_package")]
    [Description("Add a parcel to Scout's local watch list so it can be tracked later. Stores locally only; does not register the parcel with any carrier.")]
    public async Task<string> WatchPackageAsync(
        [Description("The carrier tracking number to watch, e.g. \"LX123456789GB\".")] string trackingNumber,
        [Description("Optional friendly label, e.g. \"Nike order\". Leave empty for none.")] string label = "",
        CancellationToken ct = default)
    {
        var number = Normalize(trackingNumber);
        if (!IsValidTrackingNumber(number))
        {
            return TrackingHint;
        }

        try
        {
            var trimmedLabel = string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim();
            var alreadyWatching = false;
            await _store.MutateAsync<WatchedPackagesDocument>(Collection, doc =>
            {
                if (doc.Packages.Any(p => Matches(p.TrackingNumber, number)))
                {
                    alreadyWatching = true;
                    return;
                }

                doc.Packages.Add(new WatchedPackage
                {
                    TrackingNumber = number,
                    Label = trimmedLabel,
                    AddedUtc = _clock.GetUtcNow(),
                });
            }, ct).ConfigureAwait(false);

            if (alreadyWatching)
            {
                return $"Already watching {number}.";
            }

            var labelSuffix = trimmedLabel.Length == 0 ? string.Empty : $" ({trimmedLabel})";
            return $"Now watching {number}{labelSuffix}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>List the parcels currently on the Scout-local watch list.</summary>
    /// <param name="ct">Cancels the underlying store read.</param>
    /// <returns>One watched package per line, an empty-list sentence, or a clean error line.</returns>
    [KernelFunction("list_watched_packages")]
    [Description("List the parcels currently on Scout's local watch list, with any label and when each was added.")]
    public async Task<string> ListWatchedPackagesAsync(CancellationToken ct = default)
    {
        try
        {
            var document = await _store
                .ReadOrCreateAsync<WatchedPackagesDocument>(Collection, ct).ConfigureAwait(false);
            if (document.Packages.Count == 0)
            {
                return "You are not watching any packages yet.";
            }

            return string.Join("\n", document.Packages.Select(FormatWatched));
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    /// <summary>Remove a parcel from the Scout-local watch list.</summary>
    /// <param name="trackingNumber">The carrier tracking number to stop watching.</param>
    /// <param name="ct">Cancels the underlying store write.</param>
    /// <returns>A confirmation, a not-watching note listing the rest, a corrective hint, or a clean error line.</returns>
    [KernelFunction("unwatch_package")]
    [Description("Remove a parcel from Scout's local watch list by its tracking number.")]
    public async Task<string> UnwatchPackageAsync(
        [Description("The carrier tracking number to stop watching, e.g. \"LX123456789GB\".")] string trackingNumber,
        CancellationToken ct = default)
    {
        var number = Normalize(trackingNumber);
        if (!IsValidTrackingNumber(number))
        {
            return TrackingHint;
        }

        try
        {
            var removed = false;
            var remaining = new List<string>();
            await _store.MutateAsync<WatchedPackagesDocument>(Collection, doc =>
            {
                var match = doc.Packages.FirstOrDefault(p => Matches(p.TrackingNumber, number));
                if (match is not null)
                {
                    removed = true;
                    doc.Packages.Remove(match);
                }

                remaining = doc.Packages.Select(p => p.TrackingNumber).ToList();
            }, ct).ConfigureAwait(false);

            if (removed)
            {
                return $"Stopped watching {number}.";
            }

            return remaining.Count == 0
                ? $"You were not watching {number}. Your watch list is empty."
                : $"You were not watching {number}. Watching: {string.Join(", ", remaining)}.";
        }
        catch (Exception ex) when (IsHandledFailure(ex))
        {
            return ErrorLine(ex);
        }
    }

    private static string FormatTracking(ParcelTracking tracking)
    {
        var lines = new List<string>
        {
            $"Tracking {tracking.TrackingNumber}: {Humanize(tracking.Status)}.",
        };

        if (!string.IsNullOrWhiteSpace(tracking.EstimatedDelivery))
        {
            lines.Add($"Estimated delivery: {ShortWhen(tracking.EstimatedDelivery)}.");
        }

        if (tracking.Events.Count == 0)
        {
            lines.Add("No detailed tracking events yet.");
        }
        else
        {
            lines.AddRange(tracking.Events.Take(MaxEvents).Select(FormatEvent));
        }

        return string.Join("\n", lines);
    }

    private static string FormatEvent(TrackingEvent e)
    {
        var location = string.IsNullOrWhiteSpace(e.Location) ? string.Empty : $" - {e.Location}";
        return $"{ShortWhen(e.OccurredAt)} {e.Status}{location}".Trim();
    }

    private string FormatWatched(WatchedPackage package)
    {
        var label = string.IsNullOrWhiteSpace(package.Label) ? string.Empty : $" [{package.Label}]";
        return $"{package.TrackingNumber}{label} - watching since {WatchedSince(package.AddedUtc)}";
    }

    private string WatchedSince(DateTimeOffset addedUtc)
    {
        var local = TimeZoneInfo.ConvertTime(addedUtc, ResolveZone(_timeZone));
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string Humanize(string status)
        => string.IsNullOrWhiteSpace(status) ? "status unknown" : status.Replace('_', ' ');

    // Carrier timestamps are ISO datetimes; show the date part only for a compact
    // line. This trims a machine-supplied value and is not a date parser.
    private static string ShortWhen(string value)
    {
        var index = value.IndexOf('T', StringComparison.Ordinal);
        return index > 0 ? value[..index] : value;
    }

    private static string Normalize(string trackingNumber)
        => string.IsNullOrWhiteSpace(trackingNumber)
            ? string.Empty
            : trackingNumber.Trim().ToUpperInvariant();

    private static bool Matches(string stored, string candidate)
        => string.Equals(stored, candidate, StringComparison.OrdinalIgnoreCase);

    // Tracking numbers are short alphanumeric codes; carriers occasionally include
    // hyphens. Reject anything else so obvious model mistakes get a teaching hint.
    private static bool IsValidTrackingNumber(string value)
        => value.Length is >= 4 and <= 40
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static TimeZoneInfo ResolveZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static bool IsHandledFailure(Exception ex)
        => ex is ParcelsApiException
            or HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or OperationCanceledException;

    private static string ErrorLine(Exception ex) => ex switch
    {
        ParcelsApiException api => api.Message,
        OperationCanceledException => "The parcel request timed out. Try again later.",
        IOException or UnauthorizedAccessException => "Could not access the local watch list. Try again.",
        _ => "Could not reach the parcel tracking service. Try again later.",
    };
}
