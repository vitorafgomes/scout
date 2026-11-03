namespace Scout.Operator;

/// <summary>
/// Keeps the operator's YouTube searches under the daily <c>search.list</c> bucket: at most
/// <see cref="PerDay"/> a day, counted in this process and reset at midnight Pacific time, when YouTube resets it.
/// On a host without time zone data Pacific time is a fixed UTC-8. The visual stage keeps its own instance, with its own
/// daily limit, for the pictures it makes.
/// </summary>
internal sealed class SearchBudget
{
    internal const int PerDay = 40;

    internal const int PerProject = 5;

    internal const string Spent =
        "The YouTube search budget for today (40 searches) is spent; it resets at midnight Pacific time.";

    private static readonly TimeZoneInfo SystemPacific = Pacific(TimeZoneInfo.FindSystemTimeZoneById);

    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _pacific;
    private readonly int _perDay;
    private readonly Lock _lock = new();
    private DateTime _day;
    private int _used;

    /// <summary>Count searches on <paramref name="time"/>'s clock in the system's Pacific time.</summary>
    public SearchBudget(TimeProvider time)
        : this(time, SystemPacific)
    {
    }

    /// <summary>Count up to <paramref name="perDay"/> uses a day on <paramref name="time"/>'s clock in the system's Pacific time.</summary>
    internal SearchBudget(TimeProvider time, int perDay)
        : this(time, SystemPacific, perDay)
    {
    }

    /// <summary>Count searches on <paramref name="time"/>'s clock in <paramref name="pacific"/>.</summary>
    internal SearchBudget(TimeProvider time, TimeZoneInfo pacific, int perDay = PerDay)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _pacific = pacific ?? throw new ArgumentNullException(nameof(pacific));
        _perDay = perDay;
    }

    /// <summary>Take one search from today's budget; false when today's searches are spent.</summary>
    public bool TryTake()
    {
        var today = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _pacific).Date;
        lock (_lock)
        {
            if (today != _day)
            {
                _day = today;
                _used = 0;
            }

            if (_used >= _perDay)
            {
                return false;
            }

            _used++;
            return true;
        }
    }

    /// <summary>Give back one use taken today, for a use that did not happen; nothing once the day has turned.</summary>
    public void GiveBack()
    {
        var today = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _pacific).Date;
        lock (_lock)
        {
            if (today == _day && _used > 0)
            {
                _used--;
            }
        }
    }

    /// <summary>
    /// Pacific time from <paramref name="find"/> by its IANA id, then its Windows id, else a fixed UTC-8; never throws.
    /// </summary>
    internal static TimeZoneInfo Pacific(Func<string, TimeZoneInfo> find)
    {
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            try
            {
                return find(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or System.Security.SecurityException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Pacific (UTC-8)", TimeSpan.FromHours(-8), "Pacific (UTC-8)", "Pacific (UTC-8)");
    }
}
