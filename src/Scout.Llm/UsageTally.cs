namespace Scout.Llm;

/// <summary>
/// The tokens and cost one answer used, summed over its model calls, plus one pending running count of the call in
/// progress, for a provider that repeats the count on every chunk of a call so that only its last one counts.
/// </summary>
internal sealed class UsageTally
{
    private (long? Prompt, long? Completion, long? Total)? _pending;

    public long? Prompt { get; private set; }

    public long? Completion { get; private set; }

    public long? Total { get; private set; }

    public decimal? Cost { get; private set; }

    /// <summary>
    /// Adds one model call; a call without a total adds its prompt plus completion tokens when either is known, and a
    /// call without a cost leaves the cost as it is.
    /// </summary>
    public void Add(long? prompt, long? completion, long? total, decimal? cost)
    {
        Prompt = Sum(Prompt, prompt);
        Completion = Sum(Completion, completion);
        Total = Sum(Total, total ?? (prompt is null && completion is null ? null : (prompt ?? 0) + (completion ?? 0)));
        Cost = cost is null ? Cost : (Cost ?? 0) + cost;
    }

    /// <summary>Replaces the pending running count of the call in progress.</summary>
    public void SetPending(long? prompt, long? completion, long? total) => _pending = (prompt, completion, total);

    /// <summary>Adds the pending running count as one model call and clears it; with nothing pending it does nothing.</summary>
    public void CommitPending()
    {
        if (_pending is { } call)
        {
            Add(call.Prompt, call.Completion, call.Total, null);
            _pending = null;
        }
    }

    private static long? Sum(long? sum, long? value) => value is null ? sum : (sum ?? 0) + value;
}
