namespace Scout.Plugin.Markets.Abstractions;

/// <summary>
/// A single delayed stock or index quote sourced from Stooq. Read-only projection;
/// prices are end-of-interval values and are never real time.
/// </summary>
/// <param name="Symbol">The resolved ticker, e.g. "AAPL.US" or "PETR4.SA".</param>
/// <param name="Date">The trading date the quote belongs to.</param>
/// <param name="Time">The quote time as reported, or "-" when the source omits it.</param>
/// <param name="Open">The session open price.</param>
/// <param name="High">The session high price.</param>
/// <param name="Low">The session low price.</param>
/// <param name="Close">The latest (close) price; this is the headline quote value.</param>
/// <param name="Volume">The reported traded volume, or 0 when not available.</param>
public sealed record StockQuote(
    string Symbol,
    DateOnly Date,
    string Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);
