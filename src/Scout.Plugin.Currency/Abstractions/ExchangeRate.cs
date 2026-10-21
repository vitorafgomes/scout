namespace Scout.Plugin.Currency.Abstractions;

/// <summary>
/// A single ECB reference exchange rate: how much of <paramref name="Target"/>
/// one unit of <paramref name="Base"/> buys on <paramref name="Date"/>.
/// </summary>
/// <param name="Base">Source currency ISO code, e.g. "GBP".</param>
/// <param name="Target">Target currency ISO code, e.g. "BRL".</param>
/// <param name="Rate">Units of the target currency per one unit of the base.</param>
/// <param name="Date">ECB reference date the rate was published for.</param>
public sealed record ExchangeRate(string Base, string Target, decimal Rate, DateOnly Date);
